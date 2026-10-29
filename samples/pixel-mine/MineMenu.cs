namespace PixelMine;

internal enum MineScreen
{
    Title,
    Settings,
    Playing
}

internal sealed class MineMenu(bool playing = false)
{
    private static readonly int[] s_frameLimits = [30, 60, 120, 0];
    private int _frameLimitIndex = 1;

    public MineScreen Screen { get; private set; } = playing ? MineScreen.Playing : MineScreen.Title;

    public int Selection { get; private set; }

    public int ItemCount => Screen == MineScreen.Title ? 2 : 3;

    public bool VSync { get; private set; } = true;

    public int FrameLimit => s_frameLimits[_frameLimitIndex];

    public void Select(int index) => Selection = index;

    public void Move(int direction) => Selection = (Selection + ItemCount + direction) % ItemCount;

    public void Activate(int direction = 1)
    {
        if (Screen == MineScreen.Title)
        {
            Screen = Selection == 0 ? MineScreen.Playing : MineScreen.Settings;
            Selection = 0;
        }
        else if (Screen == MineScreen.Settings && Selection == 0)
        {
            VSync = !VSync;
        }
        else if (Screen == MineScreen.Settings && Selection == 1)
        {
            _frameLimitIndex = (_frameLimitIndex + s_frameLimits.Length + direction) % s_frameLimits.Length;
        }
        else if (Screen == MineScreen.Settings)
        {
            Back();
        }
    }

    public void Back()
    {
        Screen = MineScreen.Title;
        Selection = 1;
    }
}
