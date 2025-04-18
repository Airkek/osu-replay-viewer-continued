using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osuTK.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace osu_replay_renderer_netcore.HUD
{
    /// <summary>
    /// Performance points graph. This HUD is the PP graph, which shows historical PP
    /// data (it might store some future data if you use seeking feature)
    /// </summary>
    public abstract partial class BoxOverlay : FillFlowContainer
    {
        public BoxOverlay(string title)
        {
            CornerRadius = 5;
            Masking = true;
            BorderThickness = 2.5f;
            BorderColour = new Color4(0f, 0f, 0f, 0.7f);
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Vertical;

            var bgColour = new Color4(0f, 0f, 0f, 0.6f);
            Children = new Drawable[]
            {
                new Container
                {
                    Height = 25,
                    RelativeSizeAxes = Axes.X,
                    Children = new Drawable[]
                    {
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = bgColour
                        },
                        new OsuSpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Text = title,
                            RelativeSizeAxes = Axes.X,
                            Colour = OsuColour.Gray(1.0f),
                            Margin = new MarginPadding { Left = 10f }
                        }
                    }
                }
            };
        }
    }
}
