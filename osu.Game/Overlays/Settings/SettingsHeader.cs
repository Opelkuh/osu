﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Localisation;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;

namespace osu.Game.Overlays.Settings
{
    public class SettingsHeader : Container
    {
        private readonly LocalisableString heading;
        private readonly LocalisableString subheading;

        public SettingsHeader(LocalisableString heading, LocalisableString subheading)
        {
            this.heading = heading;
            this.subheading = subheading;
        }

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider)
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;

            Children = new Drawable[]
            {
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Y,
                    RelativeSizeAxes = Axes.X,
                    Direction = FillDirection.Vertical,
                    Children = new Drawable[]
                    {
                        new OsuSpriteText
                        {
                            Text = heading,
                            Font = OsuFont.TorusAlternate.With(size: 40),
                            Margin = new MarginPadding
                            {
                                Left = SettingsPanel.CONTENT_MARGINS,
                                Top = Toolbar.Toolbar.TOOLTIP_HEIGHT
                            },
                        },
                        new OsuSpriteText
                        {
                            Colour = colourProvider.Content2,
                            Text = subheading,
                            Font = OsuFont.GetFont(size: 18),
                            Margin = new MarginPadding
                            {
                                Left = SettingsPanel.CONTENT_MARGINS,
                                Bottom = 30
                            },
                        },
                    }
                }
            };
        }
    }
}
