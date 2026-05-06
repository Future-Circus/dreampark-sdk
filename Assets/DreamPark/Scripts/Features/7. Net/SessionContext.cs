using System;

namespace DreamPark
{
    /// <summary>
    /// Static context storing the current paired session info from the backend checkin.
    /// Populated by HeadsetCheckin after successful temporal pairing.
    /// </summary>
    public static class SessionContext
    {
        public static string SessionId;
        public static string DreamboxId;
        public static string RelayHost;
        public static int? RelayPort;
        public static string[] SelectedContentIds;
        public static int? SessionLengthMinutes;
        public static bool IsPaired;

        public static event Action<SessionConfig> OnSessionPaired;

        public static void SetPaired(SessionConfig config)
        {
            SessionId = config.sessionId;
            DreamboxId = config.dreamboxId;
            RelayHost = config.relayHost;
            RelayPort = config.relayPort;
            SelectedContentIds = config.selectedContentIds;
            SessionLengthMinutes = config.sessionLengthMinutes;
            IsPaired = true;
            OnSessionPaired?.Invoke(config);
        }

        public static void Clear()
        {
            SessionId = null;
            DreamboxId = null;
            RelayHost = null;
            RelayPort = null;
            SelectedContentIds = null;
            SessionLengthMinutes = null;
            IsPaired = false;
        }
    }

    [Serializable]
    public class SessionConfig
    {
        public string sessionId;
        public string locationId;
        public string dreamboxId;
        public int headsetCount;
        public int? sessionLengthMinutes;
        public string contentId;
        public string[] selectedContentIds;
        public string relayHost;
        public int? relayPort;
        public string[] addOns;
        public string marketTier;
        public int? perMinuteRateCents;
        public string startedAt;
    }
}
