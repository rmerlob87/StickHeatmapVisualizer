using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using System.Diagnostics;
using Avalonia.Platform;
using Avalonia;
using System;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using System.IO;
using System.Linq;
using Avalonia.Media;

namespace StickHeatmapVisualizer;

public partial class MainWindow : Window
{
    private DispatcherTimer? _joystickTimer;
    private readonly WriteableBitmap _leftBitmap;
    private readonly WriteableBitmap _rightBitmap;

    private bool _recording = true;
    private bool _prevYResetState = false;

    private string _lastSwitchState = "middle"; // "low", "middle", "high"

    private readonly float[,] _leftHeatmapBuffer = new float[1000, 1000];
    private readonly float[,] _rightHeatmapBuffer = new float[1000, 1000];

    private readonly int _width = 1000;
    private readonly int _height = 1000;
    private float kernelScale = 0.1f;
    private int kernelRadius = 15;
    private float _saturation = 1.0f;

    private readonly int DeadzoneLeftXCenter = 500;
    private readonly int DeadzoneLeftXSize = 10;
    private readonly int DeadzoneLeftYCenter = 1000;
    private readonly int DeadzoneLeftYSize = 10;
    private readonly int DeadzoneRightXCenter = 500;
    private readonly int DeadzoneRightXSize = 10;
    private readonly int DeadzoneRightYCenter = 500;
    private readonly int DeadzoneRightYSize = 10;

    private bool _useLogScale = false;

    public MainWindow()
    {
        InitializeComponent();

        const int width = 1000;
        const int height = 1000;

        _leftBitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        _rightBitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        LeftHeatmapSurface.Source = _leftBitmap;
        RightHeatmapSurface.Source = _rightBitmap;

        JoystickManager.Initialize();

        _joystickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds((int)1000/250)
        };
        _joystickTimer.Tick += UpdateAndRenderHeatmaps;
        _joystickTimer.Start();

        ResetButton.Click += (_, _) => ResetHeatmaps();
        SaveButton.Click += (_, _) => SaveHeatmaps();
        LoadButton.Click += (_, _) => LoadHeatmaps();
        HelpButton.Click += (_, _) => ShowHelpWindow();
        EqualizeButton.Click += (_, _) =>
        {
            _useLogScale = !_useLogScale;
            EqualizeButton.Content = _useLogScale ? "📉Log Scale" : "📈Raw View";
        };

        this.KeyDown += OnKeyDown;
        string status = $"↑↓ Scale: {kernelScale:F2} | ←→ Radius: {kernelRadius} | Ctrl+↑↓ Saturation: {_saturation:F2}";
        EqualizeButton.Content = _useLogScale ? "📉Log Scale" : "📈Raw View";
        TuningLabel.Text = status;
        this.Focus();
    }

    private void UpdateAndRenderHeatmaps(object? sender, EventArgs e)
    {
        JoystickManager.Update();

        var (rx, ry) = JoystickManager.GetStickAxes(0, 1, flipY: true);
        var (lx, ly) = JoystickManager.GetStickAxes(3, 2, flipY: true);

        string switchState = JoystickManager.GetZSwitchState();
        StampIfRecordingAndNotIdle(rx, ry, lx, ly, switchState);

        bool resetTrigger = JoystickManager.IsYRotationTriggered();
        if (resetTrigger && !_prevYResetState)
        {
            ResetHeatmaps();
            StatusLabel.Text = "🧼 Heatmaps Reset (Y Rotation)";
        }
        _prevYResetState = resetTrigger;

        float maxLeft = MaxValue(_leftHeatmapBuffer);
        float maxRight = MaxValue(_rightHeatmapBuffer);

        DrawHeatmap(_leftBitmap, _leftHeatmapBuffer, lx, ly, 0xFFFFFFFF, maxLeft);
        LeftHeatmapSurface.InvalidateVisual();

        DrawHeatmap(_rightBitmap, _rightHeatmapBuffer, rx, ry, 0xFF00FF00, maxRight);
        RightHeatmapSurface.InvalidateVisual();
    }

    private void StampIfRecordingAndNotIdle(int rx, int ry, int lx, int ly, string switchState)
    {
        if (switchState != _lastSwitchState)
        {
            _recording = switchState == "high";
            StatusLabel.Text = _recording ? "Recording Active (Z Switch)" : "Recording Paused (Z Switch)";
            _lastSwitchState = switchState;
        }

        bool allSticksIdle = InDeadzone(lx, DeadzoneLeftXCenter, DeadzoneLeftXSize) &&
                              InDeadzone(ly, DeadzoneLeftYCenter, DeadzoneLeftYSize) &&
                              InDeadzone(rx, DeadzoneRightXCenter, DeadzoneRightXSize) &&
                              InDeadzone(ry, DeadzoneRightYCenter, DeadzoneRightYSize);

        if (allSticksIdle)
            StatusLabel.Text = "Recording Active (Z Switch) - Not recording deadzone";

        if (_recording && !allSticksIdle)
        {
            StampHeat(_leftHeatmapBuffer, lx, ly);
            StampHeat(_rightHeatmapBuffer, rx, ry);
            StatusLabel.Text = "Recording Active (Z Switch)";
        }
    }

    private unsafe void DrawHeatmap(WriteableBitmap bitmap, float[,] buffer, int dotX, int dotY, uint dotColor, float max)
    {
        using var fb = bitmap.Lock();
        var ptr = (uint*)fb.Address;

        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                float raw = buffer[x, y];
                float displayVal = _useLogScale
                    ? LogScale(raw)
                    : (max > 0 ? raw / max : 0f);  // Normalized raw

                ptr[y * _width + x] = ColorFromValue(displayVal * _saturation);
            }
        }

        DrawDot(ptr, dotX, dotY, dotColor);
    }


    private float MaxValue(float[,] buffer)
    {
        float max = 0f;
        for (int y = 0; y < _height; y++)
            for (int x = 0; x < _width; x++)
                if (buffer[x, y] > max)
                    max = buffer[x, y];
        return max;
    }

    private float LogScale(float value)
    {
        // Avoid log(0), and tweak base to control contrast
        float scaled = value * _saturation;
        float logVal = MathF.Log10(1 + scaled); // log(1 + x) keeps 0 at 0
        float logMax = MathF.Log10(1 + 1 * _saturation); // normalize to [0,1]
        return logVal / logMax;
    }

    private void ResetHeatmaps()
    {
        Array.Clear(_leftHeatmapBuffer, 0, _leftHeatmapBuffer.Length);
        Array.Clear(_rightHeatmapBuffer, 0, _rightHeatmapBuffer.Length);
    }

    private void StampHeat(float[,] buffer, int x, int y)
    {
        for (int ky = -kernelRadius; ky <= kernelRadius; ky++)
        {
            for (int kx = -kernelRadius; kx <= kernelRadius; kx++)
            {
                int px = x + kx;
                int py = y + ky;

                if (px >= 0 && px < _width && py >= 0 && py < _height)
                {
                    float distance = MathF.Sqrt(kx * kx + ky * ky);
                    float maxDist = kernelRadius;
                    float falloff = MathF.Max(0, 1 - (distance / maxDist));
                    float heat = falloff * kernelScale;

                    //buffer[px, py] = Math.Min(buffer[px, py] + heat, 1.0f);
                    buffer[px, py] += heat;
                }
            }
        }
    }

    private uint ColorFromValue(float value)
    {
        // Let the value accumulate without clamping for raw mode
        float scaled = value * _saturation;

        // Compute color with a hard-mapped gradient (you can tweak this)
        byte r = (byte)(Math.Min(scaled * 2, 1f) * 255);                // ramps up quickly
        byte g = (byte)(Math.Max(0f, Math.Min((scaled - 0.5f) * 2, 1f)) * 255); // slower ramp
        byte b = (byte)(Math.Max(0f, 1f - scaled) * 255);               // fades blue over time

        return (uint)(0xFF000000 | ((uint)r << 16) | ((uint)g << 8) | b);
    }






    // HELPERS
    private void ShowHelpWindow()
    {
        var helpText =
    @"🎮 Joystick Controls
  SB switch (Z Rotation Axis on Windows/RadiomasterPocket):
    - High: Start Recording
    - Low: Pause Recording

  SA button (Y Rotation Axis on Windows/RadiomasterPocket):
    - Push fully: Reset Heatmaps

🧑‍💻 Keyboard Controls
  ↑ / ↓     → Increase/Decrease Kernel Intensity
  ← / →     → Increase/Decrease Kernel Radius
  Ctrl + ↑↓ → Saturation Up/Down

🖱️ Buttons
  - Reset     → Clear both heatmaps
  - Save/Load → Export or import data
  - Help      → Show this dialog";

        var dialog = new Window
        {
            Title = "Help & Key Bindings",
            Width = 540,
            Height = 340,
            Content = new Avalonia.Controls.ScrollViewer
            {
                Content = new Avalonia.Controls.TextBlock
                {
                    Text = helpText,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 14,
                    Margin = new Thickness(10),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            }
        };

        dialog.ShowDialog(this);
    }

    private async void SaveHeatmaps()
    {
        var file = await this.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Heatmaps",
            SuggestedFileName = "heatmap.bin",
            FileTypeChoices = new[]
            {
            new FilePickerFileType("Binary file") { Patterns = new[] { "*.bin" } }
        }
        });

        if (file is null)
            return;

        await using var stream = await file.OpenWriteAsync();
        using var writer = new BinaryWriter(stream);

        for (int y = 0; y < _height; y++)
            for (int x = 0; x < _width; x++)
                writer.Write(_leftHeatmapBuffer[x, y]);

        for (int y = 0; y < _height; y++)
            for (int x = 0; x < _width; x++)
                writer.Write(_rightHeatmapBuffer[x, y]);
    }

    private async void LoadHeatmaps()
    {
        var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Heatmaps",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
            new FilePickerFileType("Binary file") { Patterns = new[] { "*.bin" } }
        }
        });

        var file = files.FirstOrDefault();
        if (file is null)
            return;

        await using var stream = await file.OpenReadAsync();
        using var reader = new BinaryReader(stream);

        for (int y = 0; y < _height; y++)
            for (int x = 0; x < _width; x++)
                _leftHeatmapBuffer[x, y] = reader.ReadSingle();

        for (int y = 0; y < _height; y++)
            for (int x = 0; x < _width; x++)
                _rightHeatmapBuffer[x, y] = reader.ReadSingle();
    }

    private static bool InDeadzone(int value, int center, int radius)
    {
        return Math.Abs(value - center) <= radius;
    }
    
    private unsafe void DrawDot(uint* ptr, int x, int y, uint color)
    {
        const int dotSize = 3;

        for (int dy = -dotSize; dy <= dotSize; dy++)
        {
            for (int dx = -dotSize; dx <= dotSize; dx++)
            {
                int px = x + dx;
                int py = y + dy;

                if (px >= 0 && px < _width && py >= 0 && py < _height)
                {
                    ptr[py * _width + px] = color;
                }
            }
        }
    }

    // KEYBOARD CALLBACKS
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (ctrl)
        {
            if (ctrl && e.Key == Key.Up)
                _saturation = Math.Min(_saturation + 0.5f, 1000f);
            else if (ctrl && e.Key == Key.Down)
                _saturation = Math.Max(_saturation - 0.5f, 0.00f);
        }
        else
        {
            switch (e.Key)
            {
                case Key.Up:
                    kernelScale += 0.5f;
                    break;
                case Key.Down:
                    kernelScale = Math.Max(kernelScale - 0.05f, 5.0f);
                    break;
                case Key.Right:
                    kernelRadius = Math.Min(kernelRadius + 1, 20);
                    break;
                case Key.Left:
                    kernelRadius = Math.Max(kernelRadius - 1, 1);
                    break;
            }
        }

        string status = $"↑↓ Scale: {kernelScale:F2} | ←→ Radius: {kernelRadius} | Ctrl+↑↓ Saturation: {_saturation:F2}";
        TuningLabel.Text = status;
    }

}
