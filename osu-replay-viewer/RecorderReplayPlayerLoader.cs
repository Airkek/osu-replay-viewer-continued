using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Screens.Play;
using System.Linq;
using osu_replay_renderer_netcore.CustomHosts;
using osu.Framework.Platform;

namespace osu_replay_renderer_netcore
{
    internal partial class RecorderReplayPlayerLoader : PlayerLoader
    {
        private RecorderReplayPlayer player;

        public RecorderReplayPlayerLoader(RecorderReplayPlayer player) : base(() => player)
        {
            this.player = player;
        }

        protected override void OnPlayerLoaded()
        {
            base.OnPlayerLoaded();
        }

        protected override void LoadComplete()
        {
            LoadComponent(player);
            base.LoadComplete();
            PlayerSettings.RemoveAll(v => true, true);
            
            (MetadataInfo.Children[0] as FillFlowContainer).RemoveRecursive(v => v is LoadingLayer);
            var mapMetadata = (MetadataInfo.Children[0] as FillFlowContainer).Children[5] as GridContainer;
            mapMetadata.RowDimensions = new[]
            {
                new Dimension(GridSizeMode.AutoSize),
                new Dimension(GridSizeMode.AutoSize),
                new Dimension(GridSizeMode.AutoSize),
                new Dimension()
            };
            mapMetadata.Content = new[]
            {
                mapMetadata.Content[0].ToArray(),
                mapMetadata.Content[1].ToArray(),
                CreateNewRulesetMetadata("Played by", player.GivenScore.ScoreInfo.User.Username),
                CreateNewRulesetMetadata("Ruleset", player.GivenScore.ScoreInfo.Ruleset.Name)
            };
        }

        private Drawable[] CreateNewRulesetMetadata(string c1, string c2)
        {
            return new Drawable[]
            {
                new OsuSpriteText
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Margin = new MarginPadding { Right = 5 },
                    Colour = OsuColour.Gray(0.8f),
                    Text = c1
                },
                new OsuSpriteText
                {
                    Margin = new MarginPadding { Left = 5 },
                    Text = c2
                }
            };
        }
    }
}
