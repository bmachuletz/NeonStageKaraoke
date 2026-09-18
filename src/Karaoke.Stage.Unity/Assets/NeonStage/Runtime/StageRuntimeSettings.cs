using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeonStage.Stage
{

/// <summary>Persistent device and endpoint preferences owned by the Stage player.</summary>
internal static class StageRuntimeSettings
{
    private const string AutomaticMicrophonesKey = "NeonStage.Settings.Audio.AutomaticMicrophones";
    private const string MicrophonesKey = "NeonStage.Settings.Audio.Microphones";
    private const string LiveKitOverrideKey = "NeonStage.Settings.LiveKit.ServerOverride";
    private const string DisplayIndexKey = "NeonStage.Settings.Video.DisplayIndex";
    private const string DisplayNameKey = "NeonStage.Settings.Video.DisplayName";
    private const string FullscreenKey = "NeonStage.Settings.Video.Fullscreen";
    private const char DeviceSeparator = '\u001f';

    public static bool AutomaticMicrophones
    {
        get => PlayerPrefs.GetInt(AutomaticMicrophonesKey, 1) != 0;
        set { PlayerPrefs.SetInt(AutomaticMicrophonesKey, value ? 1 : 0); PlayerPrefs.Save(); }
    }

    public static string[] SelectedMicrophones
    {
        get
        {
            var stored = PlayerPrefs.GetString(MicrophonesKey, string.Empty);
            return string.IsNullOrWhiteSpace(stored)
                ? Array.Empty<string>()
                : stored.Split(DeviceSeparator, StringSplitOptions.RemoveEmptyEntries);
        }
        set
        {
            var unique = new List<string>();
            foreach (var device in value ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(device) && !unique.Contains(device)) unique.Add(device);
            PlayerPrefs.SetString(MicrophonesKey, string.Join(DeviceSeparator.ToString(), unique));
            PlayerPrefs.Save();
        }
    }

    public static string LiveKitServerOverride
    {
        get => PlayerPrefs.GetString(LiveKitOverrideKey, string.Empty).Trim();
        set { PlayerPrefs.SetString(LiveKitOverrideKey, (value ?? string.Empty).Trim().TrimEnd('/')); PlayerPrefs.Save(); }
    }

    public static int DisplayIndex
    {
        get => Math.Max(0, PlayerPrefs.GetInt(DisplayIndexKey, 0));
        set { PlayerPrefs.SetInt(DisplayIndexKey, Math.Max(0, value)); PlayerPrefs.Save(); }
    }

    public static string DisplayName
    {
        get => PlayerPrefs.GetString(DisplayNameKey, string.Empty);
        set { PlayerPrefs.SetString(DisplayNameKey, value ?? string.Empty); PlayerPrefs.Save(); }
    }

    public static bool Fullscreen
    {
        get => PlayerPrefs.GetInt(FullscreenKey, 1) != 0;
        set { PlayerPrefs.SetInt(FullscreenKey, value ? 1 : 0); PlayerPrefs.Save(); }
    }
}

}
