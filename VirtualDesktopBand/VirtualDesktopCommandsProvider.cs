// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsDesktop;

namespace Zadjii.CmdPal.VirtualDesktops;

public partial class VirtualDesktopCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands = [];
    private readonly ICommandItem[] _bands;

    public VirtualDesktopCommandsProvider()
    {
        DisplayName = "Virtual desktops";
        Icon = Icons.AppIcon;

        Settings = VirtualDesktopSettings.Instance.Settings;
        _commands = [
            new CommandItem(new VirtualDesktopsListPage(asBand: false)) { Title = DisplayName },
        ];
        _bands = [
            new CommandItem(new VirtualDesktopsListPage(asBand: true)) { Title = DisplayName },
        ];
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }
    public override ICommandItem[]? GetDockBands()
    {
        return _bands;
    }

    public override ICommandItem? GetCommandItem(string id)
    {
        // First check top-level commands.
        foreach (var li in _commands)
        {
            if (li?.Command is ICommand cmd && cmd.Id == id)
            {
                return li;
            }
        }
        // don't need to sheck bands, those are the same thing,
        return null;
    }

}

public static class Icons
{
    public static readonly IconInfo TaskViewIcon = new("\uE7C4");

    public static readonly IconInfo CheckboxEmptyIcon = new("\uE739");
    public static readonly IconInfo CheckboxFillIcon = new("\uE73B");
    public static readonly IconInfo ToggleFilledIcon = new("\uEC11");
    public static readonly IconInfo StatusCircleIcon = new("\uEA81");
    public static readonly IconInfo CircleFillBadge12Icon = new("\uEDB0");

    public static readonly IconInfo Switchcon = new("\uE8AB"); // Switch
    public static readonly IconInfo SendIcon = new("\uE724"); // Send
    public static readonly IconInfo NewWindowIcon = new("\uE78B"); // NewWindow
    
    public static readonly IconInfo AppIcon = IconHelpers.FromRelativePath("Assets\\Square44x44Logo.scale-200.png");
}

public partial class VirtualDesktopsListPage : ListPage
{
    TaskScheduler _scheduler;

    public override string Name => "Open";
    public override string Id => "com.zadjii.virtualDesktops";
    public override IconInfo Icon => Icons.TaskViewIcon;

    public static readonly Tag CurrentDesktopTag = new("Current");

    private VirtualDesktop[] _desktops;
    private readonly bool _asBand;

    // One ListItem per desktop, reused across refreshes ONLY while everything it
    // shows is unchanged. The host keys its view models on the ListItem objects
    // we hand it: a reused item keeps its existing button (no churn), and a
    // desktop whose state changed gets a brand-new item, for which the host
    // builds a fresh view model and reads the icon/tags (the path the original
    // code always used). Items are NEVER modified after being handed out. The
    // host would only see such changes via PropChanged, and relying on that
    // (raised from inside GetItems) froze the band.
    private readonly Dictionary<Guid, CachedItem> _items = new();
    private readonly object _itemsLock = new();
    private Guid _currentDesktopId;

    private sealed record CachedItem(ListItem Item, VirtualDesktop Desktop, int Index, ItemState State);

    // Everything that affects how an item looks.
    private readonly record struct ItemState(bool IsCurrent, string IconSetting, string? Name, string? WallpaperPath);

    public VirtualDesktopsListPage(bool asBand)
    {
        _asBand = asBand;
        _scheduler = TaskScheduler.Current;

        VirtualDesktop.CurrentChanged += (_, args) => UpdateDesktopsOffUiThread();
        VirtualDesktop.Created += (_, desktop) => UpdateDesktopsOffUiThread();
        VirtualDesktop.Destroyed += (_, _) => UpdateDesktopsOffUiThread();
        VirtualDesktop.Moved += (_, _) => UpdateDesktopsOffUiThread();
        VirtualDesktop.Renamed += (_, _) => UpdateDesktopsOffUiThread();
        VirtualDesktop.WallpaperChanged += (_, _) => UpdateDesktopsOffUiThread();
        VirtualDesktopSettings.Instance.Settings.SettingsChanged += (_, _) => UpdateDesktopsOffUiThread();

        _desktops = VirtualDesktop.GetDesktops();

        ShowDetails = !_asBand;
    }

    public override IListItem[] GetItems()
    {
        VirtualDesktop[] desktops = _desktops;

        // Ask explorer before taking the lock, so no cross-process call is made
        // while holding it.
        Guid? current = TryGetCurrentDesktopId();
        string activeIcon = VirtualDesktopSettings.Instance.ActiveDesktopIcon;
        string inactiveIcon = VirtualDesktopSettings.Instance.InactiveDesktopIcon;

        lock (_itemsLock)
        {
            if (current is Guid id)
            {
                _currentDesktopId = id;
            }

            Guid currentDesktopId = _currentDesktopId;

            List<IListItem> items = new(desktops.Length);
            HashSet<Guid> seenDesktopIds = [];

            for (int i = 0; i < desktops.Length; i++)
            {
                VirtualDesktop desktop = desktops[i];
                try
                {
                    bool isCurrent = desktop.Id == currentDesktopId;

                    // The list page always shows the wallpaper, so the icon
                    // settings don't affect it.
                    string iconSetting = !_asBand ? string.Empty : isCurrent ? activeIcon : inactiveIcon;

                    items.Add(GetOrCreateItem(desktop, i, isCurrent, iconSetting));
                    seenDesktopIds.Add(desktop.Id);
                }
                catch (Exception e)
                {
                    // Defensive: skip a desktop that fails rather than failing
                    // the whole list. (Destroyed desktops don't throw here, since
                    // the wrapper's properties are cached; they drop out when the
                    // Destroyed event refreshes _desktops.)
                    DebugPrint($"GetItems: skipping desktop {i}\n{e.Message}");
                }
            }

            foreach (Guid staleId in _items.Keys.Where(id => !seenDesktopIds.Contains(id)).ToList())
            {
                _items.Remove(staleId);
            }

            return items.ToArray();
        }
    }

    private static Guid? TryGetCurrentDesktopId()
    {
        try
        {
            return VirtualDesktop.Current.Id;
        }
        catch (Exception e)
        {
            // The caller keeps the last known current desktop rather than failing GetItems.
            DebugPrint($"TryGetCurrentDesktopId\n{e.Message}");
            return null;
        }
    }

    private ListItem GetOrCreateItem(VirtualDesktop desktop, int index, bool isCurrent, string iconSetting)
    {
        // Only track the wallpaper where it is shown, so e.g. a wallpaper
        // slideshow doesn't rebuild glyph-icon band items.
        // The band doesn't show the name, so only track it for the list page.
        bool showsWallpaper = !_asBand || iconSetting == VirtualDesktopSettings.WallpaperValue;
        ItemState state = new(
            isCurrent,
            iconSetting,
            _asBand ? null : desktop.Name,
            showsWallpaper ? desktop.WallpaperPath : null);

        // Reuse the item only if nothing it shows has changed. Command IDs are
        // based on the desktop's position, so a desktop that moved gets a new
        // item. So does one whose wrapper object changed (e.g. after explorer
        // restarts), so the commands never hold a stale one.
        if (_items.TryGetValue(desktop.Id, out CachedItem? cached) &&
            cached.Index == index &&
            ReferenceEquals(cached.Desktop, desktop) &&
            cached.State == state)
        {
            return cached.Item;
        }

        ListItem item = DesktopToItem(desktop, _asBand, index, isCurrent, iconSetting);
        _items[desktop.Id] = new CachedItem(item, desktop, index, state);
        return item;
    }

    private void UpdateDesktopsOffUiThread()
    {
        Task.Factory.StartNew(UpdateDesktopsOnUiThread,
            CancellationToken.None,
            TaskCreationOptions.None,
            _scheduler);
    }

    private void UpdateDesktopsOnUiThread()
    {
        try
        {
            _desktops = VirtualDesktop.GetDesktops();
        }
        catch (Exception e)
        {
            DebugPrint($"UpdateDesktops\n{e.Message}\n{e.StackTrace}");
            return;
        }

        RaiseItemsChanged();
    }

    // Builds a complete item for the desktop's current state. The item is not
    // modified after this: when its state changes, GetOrCreateItem builds a new one.
    private static ListItem DesktopToItem(VirtualDesktop desktop, bool asBand, int index, bool isCurrent, string iconSetting)
    {
        IconInfo wallpaperIconInfo = new IconInfo(desktop.WallpaperPath);

        // Possible good icons sets:
        // * CheckboxFillIcon : CheckboxEmptyIcon for squares
        // * StatusCircleIcon : CircleFillBadge12Icon for a small circle vs big circle
        // * ToggleFilledIcon : CircleFillBadge12Icon for big oval vs circle
        // * wallpaperIconInfo : CircleFillBadge12Icon for wallpaper vs circle
        //
        // What we really should have is a setting for 
        // * active desktop icon
        // * inactive desktop icon

        IconInfo icon = asBand ?
            VirtualDesktopSettings.GetIconForValue(iconSetting, desktop.WallpaperPath) :
            wallpaperIconInfo;

        List<CommandContextItem> contextItems = [
            new CommandContextItem(new MoveWindowToDesktopCommand(desktop, index, false))
            {
                Title = "Move window here",
            },
            new CommandContextItem(new MoveWindowToDesktopCommand(desktop, index, true))
            {
                Title = "Move window and switch",
            },
        ];

        if (asBand)
        {
            // in the band we only show the context menu, not the command in the list item itself
            contextItems.Insert(0, new CommandContextItem(new SwitchToDesktopCommand(desktop, asBand: false, index))
            {
                Title = "Switch to desktop",
                Icon = Icons.Switchcon,
            });
        }

        ListItem li = new ListItem(new SwitchToDesktopCommand(desktop, asBand, index))
        {
            Icon = icon,
            MoreCommands = contextItems.ToArray(),
        };

        if (!asBand)
        {
            bool hasName = !string.IsNullOrEmpty(desktop.Name);
            string desktopNumberLabel = $"Desktop {index + 1}";

            li.Title = hasName ? desktop.Name : desktopNumberLabel;
            li.Subtitle = hasName ? desktopNumberLabel : string.Empty;
            li.Details = new Details()
            {
                Title = li.Title,
                HeroImage = icon,
            };

            if (isCurrent)
            {
                li.Tags = [CurrentDesktopTag];
            }
        }

        return li;
    }

    private static HWND FindLastNonToolWindow()
    {
        HWND found = HWND.Null;
        uint currentPid = (uint)Environment.ProcessId;

        PInvoke.EnumWindows((hWnd, _) =>
        {
            if (!PInvoke.IsWindowVisible(hWnd))
            {
                return true; // continue
            }

            const int WS_EX_TOOLWINDOW = 0x00000080;
            int exStyle = PInvoke.GetWindowLong(hWnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            {
                return true; // continue
            }

            // also skip popups
            const uint WS_POPUP = 0x80000000;
            int style = PInvoke.GetWindowLong(hWnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            if ((style & WS_POPUP) != 0)
            {
                return true; // continue
            }

            found = hWnd;
            return false; // stop
        }, IntPtr.Zero);

        return found;
    }

    private sealed partial class MoveWindowToDesktopCommand(VirtualDesktop desktop, int index, bool andSwitchTo) : InvokableCommand
    {
        public override string Name => andSwitchTo ? "Move window and switch" : "Move window here";
        public override string Id => $"com.zadjii.virtualDesktops.moveWindow.{index}";
        public override IconInfo Icon => andSwitchTo ? Icons.NewWindowIcon : Icons.SendIcon;

        public override ICommandResult Invoke()
        {
            try
            {
                HWND hWnd = FindLastNonToolWindow();
                if (hWnd != HWND.Null)
                {
                    string title = string.Empty;
                    var bufferSize = PInvoke.GetWindowTextLength(hWnd) + 1;
                    unsafe
                    {
                        fixed (char* windowNameChars = new char[bufferSize])
                        {
                            if (PInvoke.GetWindowText(hWnd, windowNameChars, bufferSize) == 0)
                            {
                                title = "<unknown>";
                            }

                            title = new string(windowNameChars);
                        }
                    }

                    DebugPrint($"Moving window {hWnd} ('{title}') to '{desktop}'");
                    VirtualDesktop.MoveToDesktop(hWnd, desktop);
                    DebugPrint($"...done");

                    if (andSwitchTo)
                    {
                        DebugPrint($"Switching to '{desktop}'");
                        desktop.Switch();
                        DebugPrint($"...done");
                    }
                }
                else
                {
                    DebugPrint("No eligible window found to move");
                }
            }
            catch (Exception e)
            {
                DebugPrint($"MoveWindowToDesktopCommand invoke\n{e.Message}\n{e.StackTrace}");
            }

            return CommandResult.KeepOpen();
        }
    }

    private sealed partial class SwitchToDesktopCommand(VirtualDesktop desktop, bool asBand, int index) : InvokableCommand
    {
        public VirtualDesktop Desktop => desktop;
        public override string Name => asBand ? string.Empty : "Switch to desktop";
        public override string Id => $"com.zadjii.virtualDesktops.switchTo.{index}";
        public override IconInfo Icon => Icons.Switchcon;
        public override string ToString()
        {
            return Desktop.ToString();
        }
        public override ICommandResult Invoke()
        {
            try
            {
                DebugPrint($"Switching to '{Desktop.ToString()}'");
                desktop.Switch();
                DebugPrint($"...done");
            }
            catch (Exception e)
            {
                DebugPrint($"SwitchToDesktopCommand invoke\n{e.Message}\n{e.StackTrace}");
            }
            return CommandResult.KeepOpen();
        }
    }

    private static void DebugPrint(string? s)
    {
        Debug.WriteLine(s);
    }
}

