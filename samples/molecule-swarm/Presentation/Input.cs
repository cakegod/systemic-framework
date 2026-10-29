using System.Numerics;

namespace MoleculeSwarm;

/// <summary>Input collected for one frame: keys are SDL scancodes.</summary>
internal sealed class InputState
{
    public const int ScancodePause = 19;
    public const int ScancodeQuit = 20;
    public const int ScancodeRestart = 21;
    public const int ScancodeDigit1 = 30;
    public const int ScancodeEscape = 41;
    public const int ScancodeSpace = 44;
    public const int ScancodeMinus = 45;
    public const int ScancodeEquals = 46;
    public const int ScancodeKpMinus = 86;
    public const int ScancodeKpPlus = 87;
    public const int ScancodeA = 4;
    public const int ScancodeD = 7;
    public const int ScancodeS = 22;
    public const int ScancodeW = 26;
    public const int ScancodeLeft = 80;
    public const int ScancodeRight = 79;
    public const int ScancodeUp = 82;
    public const int ScancodeDown = 81;

    private readonly bool[] _pressed = new bool[512];
    private readonly bool[] _down = new bool[512];
    private readonly List<Vector2> _clicks = [];

    public Vector2 Mouse { get; private set; }

    public bool Quit { get; private set; }

    public IReadOnlyList<Vector2> Clicks => _clicks;

    /// <summary>Wheel notches this frame, positive when scrolling up (zoom in).</summary>
    public int Wheel { get; private set; }

    public void BeginFrame()
    {
        Array.Clear(_pressed);
        _clicks.Clear();
        Wheel = 0;
    }

    public bool Pressed(int scancode) => scancode >= 0 && scancode < _pressed.Length && _pressed[scancode];

    /// <summary>True while the key is held, which is what panning a camera needs.</summary>
    public bool IsDown(int scancode) => scancode >= 0 && scancode < _down.Length && _down[scancode];

    internal void AddKeyDown(int scancode)
    {
        if (scancode >= 0 && scancode < _pressed.Length)
        {
            _pressed[scancode] = true;
            _down[scancode] = true;
        }
    }

    internal void AddKeyUp(int scancode)
    {
        if (scancode >= 0 && scancode < _down.Length)
        {
            _down[scancode] = false;
        }
    }

    internal void AddWheel(int notches) => Wheel += notches;

    internal void AddClick(Vector2 point) => _clicks.Add(point);

    internal void SetMouse(Vector2 point) => Mouse = point;

    internal void RequestQuit() => Quit = true;
}
