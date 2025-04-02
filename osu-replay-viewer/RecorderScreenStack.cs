using osu.Game.Graphics.Containers;
using osu.Game.Screens;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace osu_replay_renderer_netcore
{
    partial class RecorderScreenStack : OsuScreenStack
    {
        public float Parallax {
            get { return (InternalChildren[0] as ParallaxContainer).ParallaxAmount; }
            set { (InternalChildren[0] as ParallaxContainer).ParallaxAmount = value; }
        }

        public RecorderScreenStack() : base()
        {}

        protected override void LoadComplete() { Parallax = 0.0f; }
    }
}
