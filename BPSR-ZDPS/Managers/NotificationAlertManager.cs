using BPSR_ZDPS.DataTypes;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BPSR_ZDPS
{
    public static class NotificationAlertManager
    {
        static string DEFAULT_NOTIFICATION_AUDIO_FILE = Path.Combine(Utils.DATA_DIR_NAME, "Audio", "LetsDoThis.wav");

        static AudioPlayer? AudioPlayerInstance = null;
        static NotificationType NotificationEventType = NotificationType.Generic;
        static bool ShouldLoop = false;
        static bool ShouldStop = false;
        static Task? PlaybackTask = null;
        static CancellationTokenSource? CancellationTokenSource = null;

        public enum NotificationType : int
        {
            Generic = 0,
            Matchmake = 1,
            ReadyCheck = 2
        }

        public static void PlayNotifyAudio(NotificationType notificationType = NotificationType.Generic)
        {
            string audioPath = DEFAULT_NOTIFICATION_AUDIO_FILE;
            float volumeScale = 1.0f;
            NotificationEventType = notificationType;

            switch (notificationType)
            {
                case NotificationType.Matchmake:
                    if (!Settings.Instance.PlayNotificationSoundOnMatchmake)
                    {
                        return;
                    }

                    if (!string.IsNullOrEmpty(Settings.Instance.MatchmakeNotificationSoundPath) && File.Exists(Settings.Instance.MatchmakeNotificationSoundPath))
                    {
                        audioPath = Settings.Instance.MatchmakeNotificationSoundPath;
                    }

                    volumeScale = Settings.Instance.MatchmakeNotificationVolume;
                    ShouldLoop = Settings.Instance.LoopNotificationSoundOnMatchmake;
                    break;
                case NotificationType.ReadyCheck:
                    if (!Settings.Instance.PlayNotificationSoundOnReadyCheck)
                    {
                        return;
                    }

                    if (!string.IsNullOrEmpty(Settings.Instance.ReadyCheckNotificationSoundPath) && File.Exists(Settings.Instance.ReadyCheckNotificationSoundPath))
                    {
                        audioPath = Settings.Instance.ReadyCheckNotificationSoundPath;
                    }

                    volumeScale = Settings.Instance.ReadyCheckNotificationVolume;
                    ShouldLoop = Settings.Instance.LoopNotificationSoundOnReadyCheck;
                    break;
                default:
                    ShouldLoop = false;
                    break;
            }

            if (audioPath == DEFAULT_NOTIFICATION_AUDIO_FILE)
            {
                if (!File.Exists(DEFAULT_NOTIFICATION_AUDIO_FILE))
                {
                    Log.Error("Unable to locate Default Notification Audio file for NotificationAlertManager playback!");
                    return;
                }
            }
            else if (string.IsNullOrWhiteSpace(audioPath))
            {
                Log.Error("No audio file path was specified for NotificationAlertManager.PlayNotifyAudio!");
                return;
            }

            // Stop any existing playback
            StopNotifyAudio();

            // Create new audio player instance
            AudioPlayerInstance = new AudioPlayer();
            
            if (!AudioPlayerInstance.LoadWavFile(audioPath))
            {
                Log.Error($"Failed to load audio file: {audioPath}");
                AudioPlayerInstance?.Dispose();
                AudioPlayerInstance = null;
                return;
            }

            ShouldStop = false;
            CancellationTokenSource = new CancellationTokenSource();

            // Start playback task
            PlaybackTask = Task.Run(() =>
            {
                try
                {
                    // Only allow looping if we know the PlayerUID
                    bool shouldLoop = ShouldLoop && AppState.PlayerUID != 0;

                    // Play the audio
                    AudioPlayerInstance?.Play(volumeScale, loop: shouldLoop);

                    if (!shouldLoop)
                    {
                        // If not looping, wait for playback to complete
                        while (AudioPlayerInstance != null && AudioPlayerInstance.IsPlaying() && !ShouldStop)
                        {
                            Thread.Sleep(100);
                        }

                        // Cleanup
                        AudioPlayerInstance?.Dispose();
                        AudioPlayerInstance = null;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error during audio playback");
                    AudioPlayerInstance?.Dispose();
                    AudioPlayerInstance = null;
                }
            }, CancellationTokenSource.Token);
        }

        public static void StopNotifyAudio()
        {
            ShouldStop = true;
            
            CancellationTokenSource?.Cancel();
            
            if (AudioPlayerInstance != null)
            {
                AudioPlayerInstance.Stop();
                AudioPlayerInstance.Dispose();
                AudioPlayerInstance = null;
            }

            CancellationTokenSource?.Dispose();
            CancellationTokenSource = null;
            PlaybackTask = null;
        }
    }
}
