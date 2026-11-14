using System;
using UnityEngine;

namespace AITraffic.Workers
{
    // CRITICAL INVARIANT [WRK-12]: Worker alert pings reuse the vanilla Comms Radio warning clip (CommsRadioCarDeleter / RerailController.warningSound),
    // are played main-thread only via AudioSource.PlayClipAtPoint, and are rate-limited so overlapping toasts never stack beeps.
    public static class WorkerAudio
    {
        private static AudioClip _warningClip;
        private static float _lastPlayTime = -100f;
        private const float MinIntervalSeconds = 0.75f;

        public static AudioClip GetWarningClip()
        {
            if (_warningClip != null) return _warningClip;

            try
            {
                var deleters = Resources.FindObjectsOfTypeAll<DV.CommsRadioCarDeleter>();
                if (deleters != null && deleters.Length > 0 && deleters[0].warningSound != null)
                {
                    _warningClip = deleters[0].warningSound;
                    return _warningClip;
                }

                var rerails = Resources.FindObjectsOfTypeAll<DV.RerailController>();
                if (rerails != null && rerails.Length > 0 && rerails[0].warningSound != null)
                {
                    _warningClip = rerails[0].warningSound;
                    return _warningClip;
                }
            }
            catch (Exception ex)
            {
                Main.LogWarning(string.Format("[WorkerAudio] Warning clip discovery failed: {0}", ex.Message));
            }

            return null;
        }

        public static void PlayWarningSound(Vector3 position = default(Vector3))
        {
            try
            {
                if (Time.unscaledTime - _lastPlayTime < MinIntervalSeconds) return;

                var clip = GetWarningClip();
                if (clip == null) return;

                Vector3 pos = position;
                if (pos == Vector3.zero)
                {
                    if (PlayerManager.PlayerTransform == null) return;
                    pos = PlayerManager.PlayerTransform.position;
                }

                _lastPlayTime = Time.unscaledTime;
                AudioSource.PlayClipAtPoint(clip, pos, 1.0f);
            }
            catch (Exception ex)
            {
                Main.LogWarning(string.Format("[WorkerAudio] Failed to play warning sound: {0}", ex.Message));
            }
        }
    }
}
