// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Online.API.Requests.Responses;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Overlays.BeatmapListing.Panels
{
    public abstract class BeatmapPanel : OsuClickableContainer, IHasContextMenu
    {
        public readonly APIBeatmapSet SetInfo;

        private const double hover_transition_time = 400;
        private const int maximum_difficulty_icons = 10;

        private Container content;

        public PreviewTrack Preview => PlayButton.Preview;
        public IBindable<bool> PreviewPlaying => PlayButton?.Playing;

        protected abstract PlayButton PlayButton { get; }
        protected abstract Box PreviewBar { get; }

        protected virtual bool FadePlayButton => true;

        protected override Container<Drawable> Content => content;

        protected Action ViewBeatmap;

        protected BeatmapPanel(APIBeatmapSet setInfo)
            : base(HoverSampleSet.Submit)
        {
            Debug.Assert(setInfo.OnlineID > 0);

            SetInfo = setInfo;
        }

        private readonly EdgeEffectParameters edgeEffectNormal = new EdgeEffectParameters
        {
            Type = EdgeEffectType.Shadow,
            Offset = new Vector2(0f, 1f),
            Radius = 2f,
            Colour = Color4.Black.Opacity(0.25f),
        };

        private readonly EdgeEffectParameters edgeEffectHovered = new EdgeEffectParameters
        {
            Type = EdgeEffectType.Shadow,
            Offset = new Vector2(0f, 5f),
            Radius = 10f,
            Colour = Color4.Black.Opacity(0.3f),
        };

        [BackgroundDependencyLoader(permitNulls: true)]
        private void load(BeatmapManager beatmaps, OsuColour colours, BeatmapSetOverlay beatmapSetOverlay)
        {
            AddInternal(content = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                EdgeEffect = edgeEffectNormal,
                Children = new[]
                {
                    CreateBackground(),
                    new DownloadProgressBar(SetInfo)
                    {
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.BottomLeft,
                        Depth = -1,
                    },
                }
            });

            Action = ViewBeatmap = () =>
            {
                Debug.Assert(SetInfo.OnlineID > 0);
                beatmapSetOverlay?.FetchAndShowBeatmapSet(SetInfo.OnlineID);
            };
        }

        protected override void Update()
        {
            base.Update();

            if (PreviewPlaying.Value && Preview != null && Preview.TrackLoaded)
            {
                PreviewBar.Width = (float)(Preview.CurrentTime / Preview.Length);
            }
        }

        protected override bool OnHover(HoverEvent e)
        {
            content.TweenEdgeEffectTo(edgeEffectHovered, hover_transition_time, Easing.OutQuint);
            content.MoveToY(-4, hover_transition_time, Easing.OutQuint);
            if (FadePlayButton)
                PlayButton.FadeIn(120, Easing.InOutQuint);

            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            content.TweenEdgeEffectTo(edgeEffectNormal, hover_transition_time, Easing.OutQuint);
            content.MoveToY(0, hover_transition_time, Easing.OutQuint);
            if (FadePlayButton && !PreviewPlaying.Value)
                PlayButton.FadeOut(120, Easing.InOutQuint);

            base.OnHoverLost(e);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            this.FadeInFromZero(200, Easing.Out);

            PreviewPlaying.ValueChanged += playing =>
            {
                PlayButton.FadeTo(playing.NewValue || IsHovered || !FadePlayButton ? 1 : 0, 120, Easing.InOutQuint);
                PreviewBar.FadeTo(playing.NewValue ? 1 : 0, 120, Easing.InOutQuint);
            };
        }

        protected List<DifficultyIcon> GetDifficultyIcons(OsuColour colours)
        {
            var icons = new List<DifficultyIcon>();

            if (SetInfo.Beatmaps.Length > maximum_difficulty_icons)
            {
                foreach (var ruleset in SetInfo.Beatmaps.Select(b => b.Ruleset).Distinct())
                    icons.Add(new GroupedDifficultyIcon(SetInfo.Beatmaps.Where(b => b.RulesetID == ruleset.OnlineID).ToList(), ruleset, this is ListBeatmapPanel ? Color4.White : colours.Gray5));
            }
            else
            {
                foreach (var b in SetInfo.Beatmaps.OrderBy(beatmap => beatmap.RulesetID).ThenBy(beatmap => beatmap.StarRating))
                    icons.Add(new DifficultyIcon(b));
            }

            return icons;
        }

        protected Drawable CreateBackground() => new UpdateableOnlineBeatmapSetCover
        {
            RelativeSizeAxes = Axes.Both,
            OnlineInfo = SetInfo,
        };

        public class Statistic : FillFlowContainer
        {
            private readonly SpriteText text;

            private int value;

            public int Value
            {
                get => value;
                set
                {
                    this.value = value;
                    text.Text = Value.ToString(@"N0");
                }
            }

            public Statistic(IconUsage icon, int value = 0)
            {
                Anchor = Anchor.TopRight;
                Origin = Anchor.TopRight;
                AutoSizeAxes = Axes.Both;
                Direction = FillDirection.Horizontal;
                Spacing = new Vector2(5f, 0f);

                Children = new Drawable[]
                {
                    text = new OsuSpriteText { Font = OsuFont.GetFont(weight: FontWeight.SemiBold, italics: true) },
                    new SpriteIcon
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Icon = icon,
                        Shadow = true,
                        Size = new Vector2(14),
                    },
                };

                Value = value;
            }
        }

        public MenuItem[] ContextMenuItems => new MenuItem[]
        {
            new OsuMenuItem("View Beatmap", MenuItemType.Highlighted, ViewBeatmap),
        };
    }
}
