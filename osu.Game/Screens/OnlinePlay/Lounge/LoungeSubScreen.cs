﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Framework.Screens;
using osu.Framework.Threading;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osu.Game.Input;
using osu.Game.Online.Rooms;
using osu.Game.Overlays;
using osu.Game.Rulesets;
using osu.Game.Screens.Backgrounds;
using osu.Game.Screens.OnlinePlay.Components;
using osu.Game.Screens.OnlinePlay.Lounge.Components;
using osu.Game.Screens.OnlinePlay.Match;
using osu.Game.Users;
using osuTK;

namespace osu.Game.Screens.OnlinePlay.Lounge
{
    [Cached]
    public abstract class LoungeSubScreen : OnlinePlaySubScreen
    {
        public override string Title => "Lounge";

        protected override BackgroundScreen CreateBackground() => new BackgroundScreenDefault();

        protected override UserActivity InitialActivity => new UserActivity.SearchingForLobby();

        protected Container<OsuButton> Buttons { get; } = new Container<OsuButton>
        {
            Anchor = Anchor.BottomLeft,
            Origin = Anchor.BottomLeft,
            AutoSizeAxes = Axes.Both
        };

        protected ListingPollingComponent ListingPollingComponent { get; private set; }

        [Resolved]
        private Bindable<Room> selectedRoom { get; set; }

        [Resolved]
        private MusicController music { get; set; }

        [Resolved(CanBeNull = true)]
        private OngoingOperationTracker ongoingOperationTracker { get; set; }

        [Resolved]
        private IBindable<RulesetInfo> ruleset { get; set; }

        [CanBeNull]
        private IDisposable joiningRoomOperation { get; set; }

        [CanBeNull]
        private LeasedBindable<Room> selectionLease;

        private readonly Bindable<FilterCriteria> filter = new Bindable<FilterCriteria>(new FilterCriteria());
        private readonly IBindable<bool> operationInProgress = new Bindable<bool>();
        private readonly IBindable<bool> isIdle = new BindableBool();
        private LoadingLayer loadingLayer;
        private RoomsContainer roomsContainer;
        private SearchTextBox searchTextBox;
        private Dropdown<RoomStatusFilter> statusDropdown;

        [BackgroundDependencyLoader(true)]
        private void load([CanBeNull] IdleTracker idleTracker)
        {
            if (idleTracker != null)
                isIdle.BindTo(idleTracker.IsIdle);

            OsuScrollContainer scrollContainer;

            InternalChildren = new Drawable[]
            {
                ListingPollingComponent = CreatePollingComponent().With(c => c.Filter.BindTarget = filter),
                loadingLayer = new LoadingLayer(true),
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding
                    {
                        Left = WaveOverlayContainer.WIDTH_PADDING,
                        Right = WaveOverlayContainer.WIDTH_PADDING,
                    },
                    Child = new GridContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        RowDimensions = new[]
                        {
                            new Dimension(GridSizeMode.Absolute, Header.HEIGHT),
                            new Dimension(GridSizeMode.Absolute, 25),
                            new Dimension(GridSizeMode.Absolute, 20)
                        },
                        Content = new[]
                        {
                            new Drawable[]
                            {
                                searchTextBox = new LoungeSearchTextBox
                                {
                                    Anchor = Anchor.CentreRight,
                                    Origin = Anchor.CentreRight,
                                    RelativeSizeAxes = Axes.X,
                                    Width = 0.6f,
                                },
                            },
                            new Drawable[]
                            {
                                new Container
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    Depth = float.MinValue, // Contained filters should appear over the top of rooms.
                                    Children = new Drawable[]
                                    {
                                        Buttons.WithChild(CreateNewRoomButton().With(d =>
                                        {
                                            d.Anchor = Anchor.BottomLeft;
                                            d.Origin = Anchor.BottomLeft;
                                            d.Size = new Vector2(150, 37.5f);
                                            d.Action = () => Open();
                                        })),
                                        new FillFlowContainer
                                        {
                                            Anchor = Anchor.TopRight,
                                            Origin = Anchor.TopRight,
                                            AutoSizeAxes = Axes.Both,
                                            Direction = FillDirection.Horizontal,
                                            Spacing = new Vector2(10),
                                            ChildrenEnumerable = CreateFilterControls().Select(f => f.With(d =>
                                            {
                                                d.Anchor = Anchor.TopRight;
                                                d.Origin = Anchor.TopRight;
                                            }))
                                        }
                                    }
                                }
                            },
                            null,
                            new Drawable[]
                            {
                                new Container
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    Children = new Drawable[]
                                    {
                                        scrollContainer = new OsuScrollContainer
                                        {
                                            RelativeSizeAxes = Axes.Both,
                                            ScrollbarOverlapsContent = false,
                                            Child = roomsContainer = new RoomsContainer
                                            {
                                                Filter = { BindTarget = filter },
                                                SelectedRoom = { BindTarget = selectedRoom }
                                            }
                                        },
                                    }
                                },
                            }
                        }
                    },
                },
            };

            // scroll selected room into view on selection.
            selectedRoom.BindValueChanged(val =>
            {
                var drawable = roomsContainer.Rooms.FirstOrDefault(r => r.Room == val.NewValue);
                if (drawable != null)
                    scrollContainer.ScrollIntoView(drawable);
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            searchTextBox.Current.BindValueChanged(_ => updateFilterDebounced());
            ruleset.BindValueChanged(_ => UpdateFilter());

            isIdle.BindValueChanged(_ => updatePollingRate(this.IsCurrentScreen()), true);

            if (ongoingOperationTracker != null)
            {
                operationInProgress.BindTo(ongoingOperationTracker.InProgress);
                operationInProgress.BindValueChanged(_ => updateLoadingLayer());
            }

            ListingPollingComponent.InitialRoomsReceived.BindValueChanged(_ => updateLoadingLayer(), true);

            updateFilter();
        }

        #region Filtering

        public void UpdateFilter() => Scheduler.AddOnce(updateFilter);

        private ScheduledDelegate scheduledFilterUpdate;

        private void updateFilterDebounced()
        {
            scheduledFilterUpdate?.Cancel();
            scheduledFilterUpdate = Scheduler.AddDelayed(UpdateFilter, 200);
        }

        private void updateFilter()
        {
            scheduledFilterUpdate?.Cancel();
            filter.Value = CreateFilterCriteria();
        }

        protected virtual FilterCriteria CreateFilterCriteria() => new FilterCriteria
        {
            SearchString = searchTextBox.Current.Value,
            Ruleset = ruleset.Value,
            Status = statusDropdown.Current.Value
        };

        protected virtual IEnumerable<Drawable> CreateFilterControls()
        {
            statusDropdown = new SlimEnumDropdown<RoomStatusFilter>
            {
                RelativeSizeAxes = Axes.None,
                Width = 160,
            };

            statusDropdown.Current.BindValueChanged(_ => UpdateFilter());

            yield return statusDropdown;
        }

        #endregion

        public override void OnEntering(IScreen last)
        {
            base.OnEntering(last);
            onReturning();
        }

        public override void OnResuming(IScreen last)
        {
            base.OnResuming(last);

            Debug.Assert(selectionLease != null);

            selectionLease.Return();
            selectionLease = null;

            if (selectedRoom.Value?.RoomID.Value == null)
                selectedRoom.Value = new Room();

            music?.EnsurePlayingSomething();

            onReturning();
        }

        public override bool OnExiting(IScreen next)
        {
            onLeaving();
            return base.OnExiting(next);
        }

        public override void OnSuspending(IScreen next)
        {
            onLeaving();
            base.OnSuspending(next);
        }

        protected override void OnFocus(FocusEvent e)
        {
            searchTextBox.TakeFocus();
        }

        private void onReturning()
        {
            updatePollingRate(true);
            searchTextBox.HoldFocus = true;
        }

        private void onLeaving()
        {
            updatePollingRate(false);
            searchTextBox.HoldFocus = false;

            // ensure any password prompt is dismissed.
            this.HidePopover();
        }

        public void Join(Room room, string password) => Schedule(() =>
        {
            if (joiningRoomOperation != null)
                return;

            joiningRoomOperation = ongoingOperationTracker?.BeginOperation();

            RoomManager?.JoinRoom(room, password, r =>
            {
                Open(room);
                joiningRoomOperation?.Dispose();
                joiningRoomOperation = null;
            }, _ =>
            {
                joiningRoomOperation?.Dispose();
                joiningRoomOperation = null;
            });
        });

        /// <summary>
        /// Push a room as a new subscreen.
        /// </summary>
        /// <param name="room">An optional template to use when creating the room.</param>
        public void Open(Room room = null) => Schedule(() =>
        {
            // Handles the case where a room is clicked 3 times in quick succession
            if (!this.IsCurrentScreen())
                return;

            OpenNewRoom(room ?? CreateNewRoom());
        });

        protected virtual void OpenNewRoom(Room room)
        {
            selectionLease = selectedRoom.BeginLease(false);
            Debug.Assert(selectionLease != null);
            selectionLease.Value = room;

            this.Push(CreateRoomSubScreen(room));
        }

        private void updateLoadingLayer()
        {
            if (operationInProgress.Value || !ListingPollingComponent.InitialRoomsReceived.Value)
                loadingLayer.Show();
            else
                loadingLayer.Hide();
        }

        private void updatePollingRate(bool isCurrentScreen)
        {
            if (!isCurrentScreen)
                ListingPollingComponent.TimeBetweenPolls.Value = 0;
            else
                ListingPollingComponent.TimeBetweenPolls.Value = isIdle.Value ? 120000 : 15000;

            Logger.Log($"Polling adjusted (listing: {ListingPollingComponent.TimeBetweenPolls.Value})");
        }

        protected abstract OsuButton CreateNewRoomButton();

        /// <summary>
        /// Creates a new room.
        /// </summary>
        /// <returns>The created <see cref="Room"/>.</returns>
        protected abstract Room CreateNewRoom();

        protected abstract RoomSubScreen CreateRoomSubScreen(Room room);

        protected abstract ListingPollingComponent CreatePollingComponent();

        private class LoungeSearchTextBox : SearchTextBox
        {
            [BackgroundDependencyLoader]
            private void load()
            {
                BackgroundUnfocused = OsuColour.Gray(0.06f);
                BackgroundFocused = OsuColour.Gray(0.12f);
            }
        }
    }
}
