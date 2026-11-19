namespace Cnema.Services;

public static partial class SmartTitles
{
    /// <summary>
    /// What a clip can look like, for titles: the words the visual model compares frames with, and the title
    /// (English; shown through T). Only places and happenings that read the same in any game, and only ones a
    /// picture can show without guessing what anybody did.
    /// </summary>
    public static readonly (string Prompt, string Headline)[] Scenes =
    {
        ("a video game screenshot underwater with fish and coral", "Underwater"),
        ("a video game screenshot in outer space with stars and planets", "Out in space"),
        ("a video game screenshot of a big explosion with fire and smoke", "Big explosion"),
        ("a video game screenshot of a car crash", "Car crash"),
        ("a video game screenshot of a car racing on a track", "Racing"),
        ("a video game screenshot of skydiving high above the ground", "Skydiving in"),
        ("a video game screenshot of a snowy landscape", "In the snow"),
        ("a video game screenshot of a city at night with neon lights", "Night in the city"),
        ("a video game screenshot of a dark cave", "Down in the caves"),
        ("a video game screenshot of a giant boss monster", "Boss fight"),
        ("a video game screenshot of a dragon", "Dragon"),
        ("a video game screenshot of a boat on the open sea", "Out at sea"),
        ("a video game screenshot of a desert with sand dunes", "In the desert"),
        ("a video game screenshot of a sunset over the landscape", "Sunset"),
        ("a video game screenshot of a person riding a horse", "On horseback"),
        ("a video game screenshot of flying a plane or helicopter", "Up in the air"),
    };

    /// <summary>How far above "a video game screenshot" a frame must match a scene, and lead the next scene by.</summary>
    public const float SceneMargin = 0.055f, SceneLead = 0.012f;

    /// <summary>
    /// The scene a clip shows, from its frame vectors and the scenes' word vectors, or null when no scene is
    /// clearly it. Two frames must agree (or one, for a clip with only a frame or two), so one odd frame can't name it.
    /// </summary>
    public static string? SceneFrom(float[][] frames, float[][] sceneVectors, float[][] neutral)
    {
        if (frames.Length == 0 || sceneVectors.Length != Scenes.Length || neutral.Length == 0) return null;
        var votes = new int[Scenes.Length];
        var best = new float[Scenes.Length];
        foreach (var f in frames)
        {
            var baseline = neutral.Average(n => Models.VisualInfo.Dot(f, n));
            int top = -1;
            float first = float.MinValue, second = float.MinValue;
            for (var i = 0; i < sceneVectors.Length; i++)
            {
                var m = Models.VisualInfo.Dot(f, sceneVectors[i]) - baseline;
                if (m > first) { second = first; first = m; top = i; }
                else if (m > second) second = m;
            }
            if (top < 0 || first < SceneMargin || first - second < SceneLead) continue;
            votes[top]++;
            best[top] = Math.Max(best[top], first);
        }
        var need = frames.Length <= 2 ? 1 : 2;
        var winner = Enumerable.Range(0, Scenes.Length).Where(i => votes[i] >= need).OrderByDescending(i => votes[i]).ThenByDescending(i => best[i]).FirstOrDefault(-1);
        return winner < 0 ? null : T(Scenes[winner].Headline);
    }
}
