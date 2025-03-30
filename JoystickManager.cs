using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace StickHeatmapVisualizer;

public static class JoystickManager
{
    private static IntPtr _joystick = IntPtr.Zero;

    public static (bool, string) Initialize()
    {
        SDL.SDL_Init(SDL.SDL_INIT_JOYSTICK);

        string resultMessage = "🚫 No joystick detected.";

        if (SDL.SDL_NumJoysticks() > 0)
        {
            _joystick = SDL.SDL_JoystickOpen(0);

            if (_joystick == IntPtr.Zero)
            {
                resultMessage = "⚠️ Failed to open joystick.";
                return (false, resultMessage);
            }

            resultMessage = $"🎮 Joystick connected: {SDL.SDL_JoystickName(_joystick)}";
            return (true, resultMessage);
        }

        return (true, resultMessage);
    }

    public static string GetZSwitchState(int deadzone = 8192)
    {
        if (_joystick == IntPtr.Zero)
            return "middle";

        int rawZ = SDL.SDL_JoystickGetAxis(_joystick, 5); // Adjust index if needed

        if (rawZ < -deadzone)
            return "low";
        if (rawZ > deadzone)
            return "high";
        return "middle";
    }

    public static bool IsYRotationTriggered(float threshold = 0.9f)
    {
        if (_joystick == IntPtr.Zero)
            return false;

        int rawYRot = SDL.SDL_JoystickGetAxis(_joystick, 4); // Confirm axis index
        float yNorm = rawYRot / 32768f;

        return yNorm > threshold;
    }


    public static void Update()
    {
        if (_joystick != IntPtr.Zero)
        {
            SDL.SDL_JoystickUpdate();
        }
    }

    public static (int x, int y) GetStickAxes(int axisX, int axisY, bool flipY = false)
    {
        if (_joystick == IntPtr.Zero)
            return (500, 500);

        int rawX = SDL.SDL_JoystickGetAxis(_joystick, axisX);
        int rawY = SDL.SDL_JoystickGetAxis(_joystick, axisY);
        if (flipY) rawY = -rawY;

        int toRange(int val) => (int)(((val + 32768) / 65535.0) * 999);

        return (toRange(rawX), toRange(rawY));
    }
}
