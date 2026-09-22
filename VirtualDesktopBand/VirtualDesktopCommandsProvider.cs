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

    // One ListItem per desktop, reused across refreshes and updated in place.
    // The dock keys its buttons on the ListItem objects we hand it. Returning
    // brand-new items on every refresh (i.e. every desktop switch) made it
    // throw away and recycle every button each time, which could leave the
    // compact dock with blank icons until it was rebuilt via "Edit".
    private readonly Dictionary<Guid, CachedItem> _items = new();
    private readonly object _itemsLock = new();
    private Guid _currentDesktopId;

    private sealed record CachedItem(ListItem Item, VirtualDesktop Desktop, int Index);

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
        lock (_itemsLock)
        {
            VirtualDesktop[] desktops = _desktops;
            Guid currentDesktopId = GetCurrentDesktopId();

            List<IListItem> items = new(desktops.Length);
            HashSet<Guid> seenDesktopIds = [];

            for (int i = 0; i < desktops.Length; i++)
            {
                VirtualDesktop desktop = desktops[i];
                try
                {
                    items.Add(GetOrUpdateItem(desktop, i, desktop.Id == currentDesktopId));
                    seenDesktopIds.Add(desktop.Id);
                }
                catch (Exception e)
                {
                    // The desktop may have been destroyed since we enumerated
                    // it. Skip it rather than failing the whole list.
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

    private Guid GetCurrentDesktopId()
    {
        try
        {
            _currentDesktopId = VirtualDesktop.Current.Id;
        }
        catch (Exception e)
        {
            // Keep the last known current desktop rather than failing GetItems.
            DebugPrint($"GetCurrentDesktopId\n{e.Message}");
        }

        return _currentDesktopId;
    }

    private ListItem GetOrUpdateItem(VirtualDesktop desktop, int index, bool isCurrent)
    {
        // Command IDs are based on the desktop's position, so a desktop that
        // moved gets a new item. So does one whose wrapper object changed
        // (e.g. after explorer restarts), so the commands never hold a stale one.
        if (!_items.TryGetValue(desktop.Id, out CachedItem? cached) ||
            cached.Index != index ||
            !ReferenceEquals(cached.Desktop, desktop))
        {
            cached = new CachedItem(DesktopToItem(desktop, _asBand, index), desktop, index);
            _items[desktop.Id] = cached;
        }

        ApplyDesktopState(cached.Item, desktop, _asBand, index, isCurrent);
        return cached.Item;
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

    // Builds the parts of an item that don't change while the desktop stays
    // at the same position: its commands. ApplyDesktopState fills in the rest.
    private static ListItem DesktopToItem(VirtualDesktop desktop, bool asBand, int index)
    {
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

        return new ListItem(new SwitchToDesktopCommand(desktop, asBand, index))
        {
            MoreCommands = contextItems.ToArray(),
        };
    }

    // Updates the visible state of an item (icon, and for the list page its
    // title, details and tags). Only properties that actually changed are
    // set, so the dock isn't asked to reload icons that are already right.
    private static void ApplyDesktopState(ListItem li, VirtualDesktop desktop, bool asBand, int index, bool isCurrent)
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
            (isCurrent
                ? VirtualDesktopSettings.GetIconForValue(VirtualDesktopSettings.Instance.ActiveDesktopIcon, desktop.WallpaperPath)
                : VirtualDesktopSettings.GetIconForValue(VirtualDesktopSettings.Instance.InactiveDesktopIcon, desktop.WallpaperPath)) :
            wallpaperIconInfo;

        bool iconChanged = !SameIcon(li.Icon, icon);
        if (iconChanged)
        {
            li.Icon = icon;
        }

        if (!asBand)
        {
            bool hasName = !string.IsNullOrEmpty(desktop.Name);
            string desktopNumberLabel = $"Desktop {index + 1}";
            string title = hasName ? desktop.Name : desktopNumberLabel;
            string subtitle = hasName ? desktopNumberLabel : string.Empty;

            if (li.Title != title)
            {
                li.Title = title;
            }

            if (li.Subtitle != subtitle)
            {
                li.Subtitle = subtitle;
            }

            if (iconChanged || li.Details?.Title != title)
            {
                li.Details = new Details()
                {
                    Title = title,
                    HeroImage = icon,
                };
            }

            bool hasCurrentTag = li.Tags.Length > 0;
            if (isCurrent != hasCurrentTag)
            {
                if (isCurrent)
                {
                    li.Tags = [CurrentDesktopTag];
                }
                else
                {
                    li.Tags = [];
                }
            }
        }
    }

    private static bool SameIcon(IIconInfo? a, IIconInfo? b)
    {
        return ReferenceEquals(a, b) ||
            (a is not null && b is not null &&
             a.Light?.Icon == b.Light?.Icon &&
             a.Dark?.Icon == b.Dark?.Icon);
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

