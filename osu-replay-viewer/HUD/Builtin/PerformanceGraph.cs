using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Lines;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics;
using osuTK;
using osuTK.Graphics;
using System;

namespace osu_replay_renderer_netcore.HUD.Builtin
{
    // Should we split graph to seperate abstract class?

    /// <summary>
    /// Performance points graph. This HUD is the PP graph, which shows historical PP
    /// data (it might store some future data if you use seeking feature)
    /// </summary>
    public partial class PerformanceGraph : BoxOverlay
    {
        private const int NUM_OF_SEGMENTS = 1000;
        private const double MS_PER_SEGMENT = 1;

        public double[] Segments;
        public Vector2[] Vertices;
        public Path Path2D;

        public Bindable<double> PP = new BindableDouble(0);
        public double Minimum = 0, Maximum = 10;

        public GraphDisplay.GraphWindowProcessor WindowProcessor { get; set; } = GraphDisplay.WindowedClamped;
        public double CurrentRange { get => Maximum - Minimum; }
        public double WindowScale { get; set; } = 50;
        public double Smooth { get; set; } = 0.9999;

        public PerformanceGraph() : base("Performance Graph")
        {
            var bgColour = new Color4(0f, 0f, 0f, 0.6f);
            Vertices = new Vector2[NUM_OF_SEGMENTS];
            Segments = new double[NUM_OF_SEGMENTS];

            Container graph;
            Add(graph = new()
            {
                RelativeSizeAxes = Axes.X,
                Height = 100,
                Masking = true,
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = bgColour,
                    },
                    Path2D = new SmoothPath
                    {
                        PathRadius = 1.2f,
                        Colour = OsuColour.Gray(1.0f),
                        Vertices = Vertices
                    }
                },
            });

            double prevTime = 0.0;
            double prevTime2 = 0.0;
            double internalPPCounter = 0.0;

            OnUpdate += (drawable) =>
            {
                double currentTime = prevTime + Time.Elapsed;
                double timeDelta2 = currentTime - prevTime2;
                int segmentsUpdate = (int)Math.Floor(timeDelta2 / MS_PER_SEGMENT);
                if (segmentsUpdate == 0)
                {
                    prevTime = currentTime;
                    return;
                }

                // Shift
                for (int i = 0; i < NUM_OF_SEGMENTS; i++)
                {
                    double nextPP = internalPPCounter * Smooth + PP.Value * (1.0 - Smooth);
                    internalPPCounter = nextPP;
                    Segments[i] = i < (NUM_OF_SEGMENTS - segmentsUpdate) ? Segments[i + segmentsUpdate] : nextPP;
                    if (Segments[i] < Minimum) Minimum = Segments[i];
                    if (Segments[i] > Maximum) Maximum = Segments[i];
                }

                // Display
                WindowProcessor(this, internalPPCounter, out double min, out double max);
                double range = max - min;
                for (int i = 0; i < NUM_OF_SEGMENTS; i++)
                {
                    Vertices[i].X = i * DrawWidth / NUM_OF_SEGMENTS;
                    Vertices[i].Y = graph.Height - (float)((Segments[i] - min) / range) * graph.Height;
                }

                Path2D.Vertices = Vertices;
                prevTime2 = currentTime;
                prevTime = currentTime;
            };
        }

    }
}
