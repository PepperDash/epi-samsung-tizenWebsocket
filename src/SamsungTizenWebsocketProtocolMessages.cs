using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PepperDash.Essentials.Plugins.Samsung.TizenWebsocket.Protocol
{
    /// <summary>
    /// Represents a command message sent to the Samsung display.
    /// Samsung Tizen WebSocket API uses JSON-RPC 2.0 style with method, id, and params.
    /// </summary>
    [JsonObject(MemberSerialization = MemberSerialization.OptIn)]
    public class SamsungCommandMessage
    {
        /// <summary>
        /// Unique identifier for correlating responses to commands.
        /// </summary>
        [JsonProperty("id")]
        public int Id { get; set; }

        /// <summary>
        /// The method name to invoke on the display (e.g., "ms.remote.control").
        /// </summary>
        [JsonProperty("method")]
        public string Method { get; set; }

        /// <summary>
        /// The command params (e.g., { "operation": "01", "cmd": "KEY_POWER" })
        /// </summary>
        [JsonProperty("params")]
        public SamsungCommandParams Params { get; set; }

        public SamsungCommandMessage() { }

        public SamsungCommandMessage(int id, string method, SamsungCommandParams @params = null)
        {
            Id = id;
            Method = method;
            Params = @params;
        }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this, Formatting.None);
        }
    }

    /// <summary>
    /// Parameters for Samsung remote control commands.
    /// Samsung Tizen WebSocket API expects: Cmd (Click/Press/Release), DataOfCmd (key code),
    /// TypeOfRemote (SendRemoteKey), and Option.
    /// </summary>
    [JsonObject(MemberSerialization = MemberSerialization.OptIn)]
    public class SamsungCommandParams
    {
        /// <summary>
        /// Action type: "Click" for press+release, "Press" for key-down, "Release" for key-up.
        /// </summary>
        [JsonProperty("Cmd")]
        public string Cmd { get; set; }

        /// <summary>
        /// The key code (e.g., "KEY_POWEROFF", "KEY_VOLUP", "KEY_HDMI1").
        /// </summary>
        [JsonProperty("DataOfCmd")]
        public string DataOfCmd { get; set; }

        /// <summary>
        /// Remote type. Always "SendRemoteKey" for key commands.
        /// </summary>
        [JsonProperty("TypeOfRemote")]
        public string TypeOfRemote { get; set; }

        /// <summary>
        /// Option flag. Typically "false".
        /// </summary>
        [JsonProperty("Option")]
        public string Option { get; set; }

        public SamsungCommandParams() { }

        public SamsungCommandParams(string dataOfCmd, string cmd = "Click", string typeOfRemote = "SendRemoteKey", string option = "false")
        {
            Cmd = cmd;
            DataOfCmd = dataOfCmd;
            TypeOfRemote = typeOfRemote;
            Option = option;
        }
    }

    /// <summary>
    /// Represents a response message from the Samsung display.
    /// </summary>
    [JsonObject(MemberSerialization = MemberSerialization.OptIn)]
    public class SamsungResponseMessage
    {
        /// <summary>
        /// The command id this response corresponds to.
        /// Null if this is an unsolicited event.
        /// </summary>
        [JsonProperty("id")]
        public int? Id { get; set; }

        /// <summary>
        /// Result code (0 = success, non-zero = error).
        /// </summary>
        [JsonProperty("result")]
        public int? Result { get; set; }

        /// <summary>
        /// Response data (varies by command).
        /// </summary>
        [JsonProperty("data")]
        public JToken Data { get; set; }

        /// <summary>
        /// Error description if result is non-zero.
        /// </summary>
        [JsonProperty("error")]
        public string Error { get; set; }

        /// <summary>
        /// Event type if this is an unsolicited event (e.g., "power_off", "volume_changed").
        /// </summary>
        [JsonProperty("event")]
        public string Event { get; set; }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this, Formatting.None);
        }

        /// <summary>
        /// Returns true if the response indicates success (result == 0).
        /// </summary>
        public bool IsSuccess => Result.HasValue && Result.Value == 0;

        /// <summary>
        /// Returns true if this is an unsolicited event (no id).
        /// </summary>
        public bool IsEvent => !Id.HasValue && !string.IsNullOrEmpty(Event);
    }

    /// <summary>
    /// Represents the current device feedback state.
    /// Updated from polling responses and unsolicited events.
    /// </summary>
    public class SamsungTizenWebsocketFeedback
    {
        /// <summary>
        /// Power state: true = on, false = off.
        /// </summary>
        public bool? PowerOn { get; set; }

        /// <summary>
        /// Volume level (0-100).
        /// </summary>
        public int? VolumeLevel { get; set; }

        /// <summary>
        /// Mute state: true = muted, false = unmuted.
        /// </summary>
        public bool? IsMuted { get; set; }

        /// <summary>
        /// Current input source (e.g., "hdmi1", "hdmi2", "displayport", "dvi").
        /// </summary>
        public string CurrentSource { get; set; }

        /// <summary>
        /// Device temperature in Celsius (if available).
        /// </summary>
        public int? Temperature { get; set; }

        /// <summary>
        /// Timestamp when feedback was last updated.
        /// </summary>
        public DateTime LastUpdateTime { get; set; } = DateTime.Now;

        /// <summary>
        /// Device is connected and responding.
        /// </summary>
        public bool IsConnected { get; set; } = false;

        /// <summary>
        /// Device is authenticated and ready for commands.
        /// </summary>
        public bool IsAuthenticated { get; set; } = false;

        public override string ToString()
        {
            return string.Format(
                "Power={0}, Volume={1}%, Muted={2}, Source={3}, Temp={4}°C, Connected={5}, Auth={6}, LastUpdate={7:O}",
                PowerOn ?? false ? "On" : "Off",
                VolumeLevel ?? 0,
                IsMuted ?? false,
                CurrentSource ?? "N/A",
                Temperature ?? 0,
                IsConnected,
                IsAuthenticated,
                LastUpdateTime);
        }
    }

    /// <summary>
    /// Samsung command constants and codes.
    /// </summary>
    public static class SamsungTizenCommands
    {
        public const string RemoteControlMethod = "ms.remote.control";
        public const string EventNotificationMethod = "ms.channel.event";

        public const string OperationClick = "Click";
        public const string OperationPress = "Press";
        public const string OperationRelease = "Release";

        // Remote control key codes
        public const string KeyPowerOn = "KEY_POWERON";
        public const string KeyPowerOff = "KEY_POWEROFF";
        public const string KeyPower = "KEY_POWER";
        public const string KeyVolUp = "KEY_VOLUP";
        public const string KeyVolDown = "KEY_VOLDOWN";
        public const string KeyMute = "KEY_MUTE";
        public const string KeyHdmi = "KEY_HDMI";
        public const string KeyHdmi1 = "KEY_HDMI1";
        public const string KeyHdmi2 = "KEY_HDMI2";
        public const string KeyHdmi3 = "KEY_HDMI3";
        public const string KeyHdmi4 = "KEY_HDMI4";
        public const string KeyDisplayPort = "KEY_DISPLAYPORT";
        public const string KeyDvi = "KEY_DVI";

        // Status/info methods
        public const string GetInfoMethod = "ms.channel.emit";
        public const string GetInstalledAppsEvent = "ed.installedApp.get";
        public const string LaunchAppEvent = "ed.apps.launch";
    }
}
