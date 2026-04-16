using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Crestron.SimplSharpPro.DeviceSupport;
using Newtonsoft.Json.Linq;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.DeviceTypeInterfaces;
using PepperDash.Essentials.Core.Queues;
using PepperDash.Essentials.Devices.Displays;
using PepperDash.Essentials.Plugins.Samsung.TizenWebsocket.Protocol;
using TwoWayDisplayBase = PepperDash.Essentials.Devices.Common.Displays.TwoWayDisplayBase;

namespace PepperDash.Essentials.Plugin
{
	/// <summary>
	/// PepperDash Essentials plugin device for Samsung QNxx-QN990FFXZA displays
	/// controlled via the Samsung Tizen WebSocket API.
	/// </summary>
	public class SamsungTizenWebsocketController : TwoWayDisplayBase, IBasicVolumeWithFeedback,
		IInputHdmi1, IInputHdmi2, IInputHdmi3, IInputHdmi4, IInputDisplayPort1, IBridgeAdvanced,
		IHasInputs<string>, IBasicVideoMuteWithFeedback, IWarmingCooling
	{
		private readonly SamsungTizenWebsocketConfig config;
		private readonly GenericQueue receiveQueue;
		private readonly SamsungTizenWebsocketProtocolBridge protocolBridge;
		private readonly SamsungTizenWebsocketAuthentication auth;
		private Timer pollTimer;
		private Timer warmupTimer;
		private Timer cooldownTimer;
		private List<bool> inputFeedback;
		private int inputNumber;
		public List<BoolFeedback> InputFeedback { get; private set; }
		public IntFeedback InputNumberFeedback { get; private set; }

		// Device state backing fields.
		// Feedback strategy:
		//   Optimistic  — set locally on command send; may be corrected if a real Samsung event arrives later.
		//                 Joins: mute (IsMutedFeedback), volume-delta (VolumeLevelFeedback), source (CurrentSourceFeedback).
		//   Event-driven — updated from unsolicited Samsung WebSocket messages parsed in ProcessFeedbackMessage.
		//                  Joins: power (PowerIsOnFeedback)*, source (CurrentSourceFeedback)*, mute (IsMutedFeedback)*.
		//                  * only when the display emits the relevant event; not guaranteed on all QN firmware.
		private bool powerIsOn;      // Event-driven when Samsung emits power event; otherwise warmer/cooldown timer
		private bool isMuted;        // Optimistic (flipped on MuteToggle); corrected by TryApplyMute if event arrives
		private bool isWarmingUp;
		private bool isCoolingDown;
		private int volumeLevel;     // Optimistic (±1 on key); corrected by TryApplyVolume if event arrives
		private string currentSource; // Optimistic (set in SendInputCommand); corrected by TryApplySource if event arrives

		#region Feedbacks

		/// <summary>Reports mute state to bridge.</summary>
		public BoolFeedback IsMutedFeedback { get; private set; }

		/// <summary>Reports WebSocket connection/online state to bridge.</summary>
		public BoolFeedback IsOnlineFeedback { get; private set; }

		/// <summary>Reports volume level (0-100) to bridge.</summary>
		public IntFeedback VolumeLevelFeedback { get; private set; }

		/// <summary>Reports current source name to bridge.</summary>
		public StringFeedback CurrentSourceFeedback { get; private set; }

		/// <summary>Video mute feedback.</summary>
		public BoolFeedback VideoMuteIsOn { get; private set; }

		/// <summary>IBasicVolumeWithFeedback mute feedback contract.</summary>
		public BoolFeedback MuteFeedback => IsMutedFeedback;

		/// <summary>IHasInputs contract.</summary>
		public ISelectableItems<string> Inputs { get; private set; }

		#endregion

		protected override Func<bool> PowerIsOnFeedbackFunc => () => powerIsOn;
		protected override Func<string> CurrentInputFeedbackFunc => () => currentSource ?? string.Empty;
		protected override Func<bool> IsWarmingUpFeedbackFunc => () => isWarmingUp;
		protected override Func<bool> IsCoolingDownFeedbackFunc => () => isCoolingDown;

		/// <summary>
		/// Constructor for Samsung Tizen WebSocket display controller.
		/// </summary>
		public SamsungTizenWebsocketController(string key, string name, SamsungTizenWebsocketConfig config)
			: base(key, name)
		{
			this.LogInformation("Constructing new {0} instance", name);

			this.config = config;
			receiveQueue = new GenericQueue(key + "-rxqueue");

			protocolBridge = new SamsungTizenWebsocketProtocolBridge(key, config.GetAddress(), config.GetPort(), config.UseSecureWebSocket());
			protocolBridge.LoadToken();
			auth = new SamsungTizenWebsocketAuthentication(key, protocolBridge);

			IsMutedFeedback = new BoolFeedback("mute", () => isMuted);
			IsOnlineFeedback = new BoolFeedback("online", () => protocolBridge.IsConnected);
			VolumeLevelFeedback = new IntFeedback("volume", () => volumeLevel);
			CurrentSourceFeedback = new StringFeedback("source", () => currentSource ?? string.Empty);
			VideoMuteIsOn = IsMutedFeedback;
			InputNumberFeedback = new IntFeedback("inputNumber", () => inputNumber);

			SetupInputs();

			protocolBridge.OnConnectionStateChanged += ProtocolBridge_OnConnectionStateChanged;
			protocolBridge.OnResponseReceived += ProtocolBridge_OnResponseReceived;
			protocolBridge.OnInfoMessage += (s, msg) => this.LogInformation(msg);
			protocolBridge.OnVerboseMessage += (s, msg) => this.LogVerbose(msg);
			protocolBridge.OnError += ProtocolBridge_OnError;
		}

		#region Overrides of EssentialsBridgeableDevice

		public override void Initialize()
		{
			this.LogInformation("Activating {0}; connecting to {1}:{2} via {3}",
				Name, protocolBridge.HostAddress, protocolBridge.Port,
				(config.UseSecureWebSocket() ? "wss" : "ws"));

			// Fire connection in background; don't block on it
			_ = protocolBridge.ConnectAsync();

			StartPollTimer();
			base.Initialize();
		}

		/// <summary>
		/// Starts the WebSocket connection on device activation.
		/// </summary>
		public override bool CustomActivate()
		{

			return base.CustomActivate();
		}

		/// <summary>
		/// Links the plugin device to the EISC bridge.
		/// </summary>
		public void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
		{
			var joinMap = new SamsungTizenWebsocketBridgeJoinMap(joinStart);

			bridge?.AddJoinMap(Key, joinMap);

			var customJoins = JoinMapHelper.TryGetJoinMapAdvancedForDevice(joinMapKey);
			if (customJoins != null)
				joinMap.SetCustomJoinData(customJoins);

			this.LogDebug("Linking to Trilist {id}", trilist.ID.ToString("X"));
			this.LogInformation("Linking to Bridge Type {type}", GetType().Name);

			// Serial
			trilist.SetString(joinMap.DeviceName.JoinNumber, Name);
			CurrentSourceFeedback.LinkInputSig(trilist.StringInput[joinMap.CurrentSource.JoinNumber]);

			// Power
			trilist.SetSigTrueAction(joinMap.PowerOff.JoinNumber, PowerOff);
			trilist.SetSigTrueAction(joinMap.PowerOn.JoinNumber, PowerOn);
			PowerIsOnFeedback.LinkInputSig(trilist.BooleanInput[joinMap.PowerOn.JoinNumber]);
			PowerIsOnFeedback.LinkComplementInputSig(trilist.BooleanInput[joinMap.PowerOff.JoinNumber]);

			// Mute
			trilist.SetSigTrueAction(joinMap.MuteToggle.JoinNumber, MuteToggle);
			IsMutedFeedback.LinkInputSig(trilist.BooleanInput[joinMap.MuteToggle.JoinNumber]);

			// Volume
			trilist.SetSigTrueAction(joinMap.VolumeUp.JoinNumber, () => VolumeUp(false));
			trilist.SetSigTrueAction(joinMap.VolumeDown.JoinNumber, () => VolumeDown(false));
			VolumeLevelFeedback.LinkInputSig(trilist.UShortInput[joinMap.VolumeLevel.JoinNumber]);

			// Inputs
			trilist.SetSigTrueAction(joinMap.InputHdmi1.JoinNumber, InputHdmi1);
			trilist.SetSigTrueAction(joinMap.InputHdmi2.JoinNumber, InputHdmi2);
			trilist.SetSigTrueAction(joinMap.InputHdmi3.JoinNumber, InputHdmi3);
			trilist.SetSigTrueAction(joinMap.InputHdmi4.JoinNumber, InputHdmi4);
			trilist.SetSigTrueAction(joinMap.InputDisplayPort.JoinNumber, InputDisplayPort);

			for (var i = 0; i < InputPorts.Count; i++)
			{
				var inputIndex = i;
				var input = InputPorts.ElementAt(inputIndex);

				if (input == null) continue;

				trilist.SetSigTrueAction((ushort)(joinMap.InputSelectOffset.JoinNumber + inputIndex), () =>
				{
					SetInput = inputIndex + 1;
				});

				var inputName = input.Key;
				if (Inputs?.Items != null && input.FeedbackMatchObject is string sourceKey && Inputs.Items.TryGetValue(sourceKey, out var selectableItem))
				{
					inputName = selectableItem.Name;
				}

				trilist.StringInput[(ushort)(joinMap.InputNamesOffset.JoinNumber + inputIndex)].StringValue = inputName ?? string.Empty;

				if (InputFeedback != null && inputIndex < InputFeedback.Count)
				{
					InputFeedback[inputIndex].LinkInputSig(trilist.BooleanInput[(ushort)(joinMap.InputSelectOffset.JoinNumber + inputIndex)]);
				}
			}

			trilist.SetUShortSigAction(joinMap.InputSelect.JoinNumber, analogValue =>
			{
				SetInput = analogValue;
			});
			InputNumberFeedback.LinkInputSig(trilist.UShortInput[joinMap.InputSelect.JoinNumber]);

			// Online
			IsOnlineFeedback.LinkInputSig(trilist.BooleanInput[joinMap.IsOnline.JoinNumber]);

			UpdateFeedbacks();

			trilist.OnlineStatusChange += (o, a) =>
			{
				if (!a.DeviceOnLine) return;
				trilist.SetString(joinMap.DeviceName.JoinNumber, Name);
				UpdateFeedbacks();

				for (var i = 0; i < InputPorts.Count; i++)
				{
					var inputIndex = i;
					if (InputFeedback != null && inputIndex < InputFeedback.Count)
						InputFeedback[inputIndex].FireUpdate();
				}
			};
		}

		#endregion

		#region Power

		/// <summary>Turns the display on.</summary>
		public override void PowerOn()
		{
			if (!protocolBridge.IsConnected)
			{
				this.LogWarning("Cannot power on because protocol bridge is not connected.");
			}

			this.LogInformation("PowerOn");
			protocolBridge.SendKey(SamsungTizenCommands.KeyPower);
			SetCoolingDown(false);
			if (!powerIsOn)
				SetWarmingUp(true);
		}

		/// <summary>Turns the display off.</summary>
		public override void PowerOff()
		{
			this.LogInformation("PowerOff");
			protocolBridge.SendKey(SamsungTizenCommands.KeyPower);
			SetWarmingUp(false);
			if (powerIsOn)
			{
				powerIsOn = false;
				PowerIsOnFeedback.FireUpdate();
			}
			SetCoolingDown(true);
		}

		/// <summary>Toggles display power state.</summary>
		public override void PowerToggle()
		{
			if (powerIsOn) PowerOff(); else PowerOn();
		}

		public override void ExecuteSwitch(object selector)
		{
			if (selector is Action action)
			{
				if (!powerIsOn)
				{
					PowerOn();
				}

				action();
				return;
			}

			if (selector is string sourceKey)
			{
				switch (sourceKey.ToLowerInvariant())
				{
					case "hdmi1": InputHdmi1(); break;
					case "hdmi2": InputHdmi2(); break;
					case "hdmi3": InputHdmi3(); break;
					case "hdmi4": InputHdmi4(); break;
					case "displayport":
					case "dp":
						InputDisplayPort1();
						break;
				}
			}
		}

		#endregion

		#region Volume

		/// <summary>
		/// Steps volume up. Sends KEY_VOLUP, then increments <c>volumeLevel</c> by 1 optimistically.
		/// If the display emits a volume event, <see cref="TryApplyVolume"/> will correct to the real value.
		/// </summary>
		public void VolumeUp(bool pressRelease)
		{
			if (pressRelease) return;
			protocolBridge.SendKey(SamsungTizenCommands.KeyVolUp);
			SetLocalVolume(volumeLevel + 1);
		}

		/// <summary>
		/// Steps volume down. Sends KEY_VOLDOWN, then decrements <c>volumeLevel</c> by 1 optimistically.
		/// If the display emits a volume event, <see cref="TryApplyVolume"/> will correct to the real value.
		/// </summary>
		public void VolumeDown(bool pressRelease)
		{
			if (pressRelease) return;
			protocolBridge.SendKey(SamsungTizenCommands.KeyVolDown);
			SetLocalVolume(volumeLevel - 1);
		}

		/// <summary>
		/// Toggles mute. Sends the KEY_MUTE key, then flips <c>isMuted</c> optimistically.
		/// If the display emits a mute-state event, <see cref="TryApplyMute"/> will correct the value.
		/// </summary>
		public void MuteToggle()
		{
			protocolBridge.SendKey(SamsungTizenCommands.KeyMute);
			isMuted = !isMuted;
			IsMutedFeedback.FireUpdate();
		}

		public void MuteOn()
		{
			if (!isMuted)
				MuteToggle();
		}

		public void MuteOff()
		{
			if (isMuted)
				MuteToggle();
		}

		public void SetVolume(ushort level)
		{
			SetLocalVolume(level);
		}

		private void SetLocalVolume(int level)
		{
			var clamped = Math.Max(0, Math.Min(100, level));
			if (volumeLevel == clamped)
				return;

			volumeLevel = clamped;
			VolumeLevelFeedback.FireUpdate();
		}

		public void VideoMuteToggle() => MuteToggle();
		public void VideoMuteOn() => MuteOn();
		public void VideoMuteOff() => MuteOff();

		#endregion

		#region Inputs

		/// <summary>
		/// Sends an arbitrary key code to the display. Callable from Essentials console:
		/// DEVJSON:9 {"deviceKey":"display-1","methodName":"SendKey","params":["KEY_HDMI1"]}
		/// </summary>
		public void SendKey(string keyCode)
		{
			this.LogInformation("SendKey: {0}", keyCode);
			protocolBridge.SendKey(keyCode);
		}

		public void InputHdmi1() => SendInputCommand("hdmi1", SamsungTizenCommands.KeyHdmi1);
		public void InputHdmi2() => SendInputCommand("hdmi2", SamsungTizenCommands.KeyHdmi2);
		public void InputHdmi3() => SendInputCommand("hdmi3", SamsungTizenCommands.KeyHdmi3);
		public void InputHdmi4() => SendInputCommand("hdmi4", SamsungTizenCommands.KeyHdmi4);
		public void InputDisplayPort() => SendInputCommand("displayport", SamsungTizenCommands.KeyDisplayPort);
		public void InputDisplayPort1() => InputDisplayPort();

		/// <summary>
		/// Sends an input-select key to the display.
		/// Sets local state and fires feedback optimistically before the key is transmitted.
		/// If the display later emits a source-change event, <see cref="TryApplySource"/> will
		/// overwrite the optimistic value with the confirmed source.
		/// </summary>
		private void SendInputCommand(string sourceKey, string command)
		{
			currentSource = sourceKey;
			CurrentSourceFeedback.FireUpdate();
			UpdateInputFeedbackBySource(sourceKey);
			protocolBridge.SendKey(command);
		}

		private int SetInput
		{
			set
			{
				if (value <= 0 || value > InputPorts.Count)
					return;

				var port = GetInputPort(value - 1);
				if (port == null)
					return;

				if (port.Selector is Action action)
					ExecuteSwitch(action);
			}
		}

		private RoutingInputPort GetInputPort(int index)
		{
			if (index < 0 || index >= InputPorts.Count)
				return null;

			return InputPorts.ElementAt(index);
		}

		private void AddRoutingInputPort(RoutingInputPort port, string feedbackMatch)
		{
			port.FeedbackMatchObject = feedbackMatch;
			InputPorts.Add(port);
		}

		private void SetupInputs()
		{
			AddRoutingInputPort(
				new RoutingInputPort(RoutingPortNames.HdmiIn1, eRoutingSignalType.Audio | eRoutingSignalType.Video,
					eRoutingPortConnectionType.Hdmi, new Action(InputHdmi1), this),
				"hdmi1");
			AddRoutingInputPort(
				new RoutingInputPort(RoutingPortNames.HdmiIn2, eRoutingSignalType.Audio | eRoutingSignalType.Video,
					eRoutingPortConnectionType.Hdmi, new Action(InputHdmi2), this),
				"hdmi2");
			AddRoutingInputPort(
				new RoutingInputPort(RoutingPortNames.HdmiIn3, eRoutingSignalType.Audio | eRoutingSignalType.Video,
					eRoutingPortConnectionType.Hdmi, new Action(InputHdmi3), this),
				"hdmi3");
			AddRoutingInputPort(
				new RoutingInputPort(RoutingPortNames.HdmiIn4, eRoutingSignalType.Audio | eRoutingSignalType.Video,
					eRoutingPortConnectionType.Hdmi, new Action(InputHdmi4), this),
				"hdmi4");
			AddRoutingInputPort(
				new RoutingInputPort(RoutingPortNames.DisplayPortIn, eRoutingSignalType.Audio | eRoutingSignalType.Video,
					eRoutingPortConnectionType.DisplayPort, new Action(InputDisplayPort1), this),
				"displayport");

			Inputs = new SamsungTizenWebsocketInputs
			{
				Items = new Dictionary<string, ISelectableItem>
				{
					{ "hdmi1", new SamsungTizenWebsocketInput("hdmi1", "HDMI 1", this) },
					{ "hdmi2", new SamsungTizenWebsocketInput("hdmi2", "HDMI 2", this) },
					{ "hdmi3", new SamsungTizenWebsocketInput("hdmi3", "HDMI 3", this) },
					{ "hdmi4", new SamsungTizenWebsocketInput("hdmi4", "HDMI 4", this) },
					{ "displayport", new SamsungTizenWebsocketInput("displayport", "DisplayPort", this) }
				}
			};

			ApplyFriendlyNames(config);

			inputFeedback = new List<bool>(new bool[InputPorts.Count + 1]);
			InputFeedback = new List<BoolFeedback>();
			for (var i = 0; i < InputPorts.Count; i++)
			{
				var fbIndex = i + 1;
				InputFeedback.Add(new BoolFeedback(string.Format("inputFb{0}", fbIndex), () => inputFeedback[fbIndex]));
			}
		}

		private void ApplyFriendlyNames(SamsungTizenWebsocketConfig properties)
		{
			if (properties?.FriendlyNames == null || Inputs?.Items == null)
				return;

			foreach (var friendly in properties.FriendlyNames)
			{
				if (string.IsNullOrEmpty(friendly?.InputKey))
					continue;

				if (friendly.HideInput)
				{
					Inputs.Items.Remove(friendly.InputKey);
					continue;
				}

				if (string.IsNullOrEmpty(friendly.Name))
					continue;

				if (Inputs.Items.TryGetValue(friendly.InputKey, out var existing))
				{
					Inputs.Items[friendly.InputKey] = new SamsungTizenWebsocketInput(existing.Key, friendly.Name, this);
				}
			}
		}

		#endregion

		#region Polling

		/// <summary>
		/// Maintains the session lifecycle for the Samsung remote-control WebSocket.
		/// Consumer Samsung displays do not expose reliable getters for power, volume, mute, or source
		/// on the <c>samsung.remote.control</c> channel, so polling is limited to token refresh and
		/// connection-state maintenance. Feedback accuracy relies on:
		/// <list type="bullet">
		///   <item><term>Optimistic local state</term><description>set immediately on mute, volume, and source commands.</description></item>
		///   <item><term>Unsolicited Samsung events</term><description>parsed by <see cref="ProcessFeedbackMessage"/> and applied by <see cref="TryApplyPower"/>, <see cref="TryApplyMute"/>, <see cref="TryApplyVolume"/>, <see cref="TryApplySource"/>.</description></item>
		///   <item><term>Warm/cool timers</term><description>infer power-on after <see cref="SamsungTizenWebsocketConfig.WarmingTimeMs"/>; power-off feedback fires immediately on command.</description></item>
		/// </list>
		/// </summary>
		public void Poll()
		{
			if (!protocolBridge.IsConnected)
				return;

			var _ = auth.RefreshTokenAsync();
			IsOnlineFeedback.FireUpdate();
		}

		private void StartPollTimer()
		{
			pollTimer?.Dispose();
			var interval = (int)(config.PollIntervalMs > 0 ? config.PollIntervalMs : 5000);
			pollTimer = new Timer(_ => Poll(), null, interval, interval);
		}

		private void StopPollTimer()
		{
			pollTimer?.Dispose();
			pollTimer = null;
			warmupTimer?.Dispose();
			warmupTimer = null;
			cooldownTimer?.Dispose();
			cooldownTimer = null;
		}

		private void SetWarmingUp(bool value)
		{
			if (isWarmingUp == value)
				return;

			isWarmingUp = value;
			warmupTimer?.Dispose();
			warmupTimer = null;

			if (value)
			{
				var warmupTime = config.WarmingTimeMs > 0 ? (int)config.WarmingTimeMs : 10000;
				warmupTimer = new Timer(_ =>
				{
					isWarmingUp = false;
					if (!powerIsOn)
					{
						powerIsOn = true;
						PowerIsOnFeedback.FireUpdate();
					}
					IsWarmingUpFeedback.FireUpdate();
					Poll();
				}, null, warmupTime, Timeout.Infinite);
			}

			IsWarmingUpFeedback.FireUpdate();
		}

		private void SetCoolingDown(bool value)
		{
			if (isCoolingDown == value)
				return;

			isCoolingDown = value;
			cooldownTimer?.Dispose();
			cooldownTimer = null;

			if (value)
			{
				var cooldownTime = config.CoolingTimeMs > 0 ? (int)config.CoolingTimeMs : 8000;
				cooldownTimer = new Timer(_ =>
				{
					isCoolingDown = false;
					IsCoolingDownFeedback.FireUpdate();
				}, null, cooldownTime, Timeout.Infinite);
			}

			IsCoolingDownFeedback.FireUpdate();
		}

		#endregion

		#region Protocol Bridge Handlers

		private void ProtocolBridge_OnConnectionStateChanged(object sender, string state)
		{
			this.LogInformation("Connection state changed: {state}", state);
			IsOnlineFeedback.FireUpdate();

			if (state == "Connected")
			{
				var _ = auth.AuthenticateAsync().ContinueWith(t =>
				{
					if (t.Result)
					{
						Poll();
					}
				});
			}
			else if (state == "Disconnected")
			{
				powerIsOn = false;
				SetWarmingUp(false);
				SetCoolingDown(false);
				UpdateFeedbacks();
			}
		}

		private void ProtocolBridge_OnResponseReceived(object sender, SamsungResponseMessage response)
		{
			receiveQueue.Enqueue(new ProcessStringMessage(response.ToString(), ProcessFeedbackMessage));
		}

		private void ProtocolBridge_OnError(object sender, Exception ex)
		{
			this.LogError("Protocol error: {message}\n{stackTrace}", ex.Message, ex.StackTrace);
		}

		/// <summary>
		/// Parses a Samsung API response JSON and updates device state feedbacks.
		/// </summary>
		private void ProcessFeedbackMessage(string json)
		{
			if (string.IsNullOrWhiteSpace(json)) return;

			try
			{
				var root = JObject.Parse(json);
				var data = root["data"] as JObject;
				var payload = data ?? root;

				var powerUpdated = TryApplyPower(payload, root["event"]?.Value<string>());
				var muteUpdated = TryApplyMute(payload, root["event"]?.Value<string>());
				var volumeUpdated = TryApplyVolume(payload);
				var sourceUpdated = TryApplySource(payload);

				if (powerUpdated) PowerIsOnFeedback.FireUpdate();
				if (muteUpdated) IsMutedFeedback.FireUpdate();
				if (volumeUpdated) VolumeLevelFeedback.FireUpdate();
				if (sourceUpdated) CurrentSourceFeedback.FireUpdate();
			}
			catch (Exception ex)
			{
				this.LogVerbose("Failed to parse Samsung feedback JSON: {message}", ex.Message);
			}
		}

		private bool TryApplyPower(JObject payload, string eventName)
		{
			var powerToken = payload["power"] ?? payload["powerState"] ?? payload["state"];
			var parsed = ParseBool(powerToken);

			if (!parsed.HasValue && !string.IsNullOrEmpty(eventName))
			{
				if (eventName.IndexOf("power_on", StringComparison.OrdinalIgnoreCase) >= 0)
					parsed = true;
				if (eventName.IndexOf("power_off", StringComparison.OrdinalIgnoreCase) >= 0)
					parsed = false;
			}

			if (!parsed.HasValue) return false;
			if (powerIsOn == parsed.Value) return false;

			powerIsOn = parsed.Value;
			if (powerIsOn)
			{
				SetWarmingUp(false);
			}
			else
			{
				SetCoolingDown(false);
			}
			return true;
		}

		private bool TryApplyMute(JObject payload, string eventName)
		{
			var muteToken = payload["mute"] ?? payload["muted"] ?? payload["isMuted"];
			var parsed = ParseBool(muteToken);

			if (!parsed.HasValue && !string.IsNullOrEmpty(eventName) &&
				eventName.IndexOf("mute", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				parsed = eventName.IndexOf("off", StringComparison.OrdinalIgnoreCase) < 0;
			}

			if (!parsed.HasValue) return false;
			if (isMuted == parsed.Value) return false;

			isMuted = parsed.Value;
			return true;
		}

		private bool TryApplyVolume(JObject payload)
		{
			var volumeToken = payload["volume"] ?? payload["volumeLevel"];
			var parsed = ParseInt(volumeToken);

			if (!parsed.HasValue) return false;
			var bounded = Math.Max(0, Math.Min(100, parsed.Value));
			if (volumeLevel == bounded) return false;

			volumeLevel = bounded;
			return true;
		}

		private bool TryApplySource(JObject payload)
		{
			var source = payload["source"]?.Value<string>()
				?? payload["input"]?.Value<string>()
				?? payload["currentSource"]?.Value<string>();

			if (string.IsNullOrEmpty(source)) return false;

			var normalizedSource = NormalizeSourceKey(source);
			if (string.Equals(currentSource, normalizedSource, StringComparison.OrdinalIgnoreCase)) return false;

			currentSource = normalizedSource;
			UpdateInputFeedbackBySource(normalizedSource);
			return true;
		}

		private static string NormalizeSourceKey(string source)
		{
			if (string.IsNullOrWhiteSpace(source))
				return string.Empty;

			var normalized = source.Trim().ToLowerInvariant()
				.Replace("_", string.Empty)
				.Replace("-", string.Empty)
				.Replace(" ", string.Empty);

			switch (normalized)
			{
				case "hdmi":
				case "hdmi1":
					return "hdmi1";
				case "hdmi2":
					return "hdmi2";
				case "hdmi3":
					return "hdmi3";
				case "hdmi4":
					return "hdmi4";
				case "displayport":
				case "displayport1":
				case "dp":
					return "displayport";
				default:
					return normalized;
			}
		}

		private void UpdateInputFeedbackBySource(string source)
		{
			if (InputPorts == null || inputFeedback == null)
				return;

			var normalized = NormalizeSourceKey(source);
			var matchedIndex = -1;

			for (var i = 0; i < InputPorts.Count; i++)
			{
				var sourceKey = InputPorts[i].FeedbackMatchObject as string;
				var selected = string.Equals(sourceKey, normalized, StringComparison.OrdinalIgnoreCase);
				inputFeedback[i + 1] = selected;

				if (selected)
					matchedIndex = i + 1;
			}

			if (matchedIndex > 0)
				inputNumber = matchedIndex;

			InputNumberFeedback.FireUpdate();

			if (InputFeedback != null)
			{
				for (var i = 0; i < InputFeedback.Count; i++)
					InputFeedback[i].FireUpdate();
			}

			if (Inputs?.Items != null)
			{
				foreach (var kvp in Inputs.Items)
				{
					kvp.Value.IsSelected = string.Equals(kvp.Key, normalized, StringComparison.OrdinalIgnoreCase);
				}

				Inputs.CurrentItem = normalized;
			}
		}

		private static bool? ParseBool(JToken token)
		{
			if (token == null) return null;

			if (token.Type == JTokenType.Boolean)
				return token.Value<bool>();

			if (token.Type == JTokenType.Integer)
				return token.Value<int>() != 0;

			var value = token.Value<string>();
			if (string.IsNullOrEmpty(value)) return null;

			if (value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
				value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
				value.Equals("1", StringComparison.OrdinalIgnoreCase))
				return true;

			if (value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
				value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
				value.Equals("0", StringComparison.OrdinalIgnoreCase))
				return false;

			return null;
		}

		private static int? ParseInt(JToken token)
		{
			if (token == null) return null;

			if (token.Type == JTokenType.Integer)
				return token.Value<int>();

			if (int.TryParse(token.Value<string>(), out var value))
				return value;

			return null;
		}

		#endregion

		private void UpdateFeedbacks()
		{
			PowerIsOnFeedback.FireUpdate();
			IsWarmingUpFeedback.FireUpdate();
			IsCoolingDownFeedback.FireUpdate();
			IsMutedFeedback.FireUpdate();
			IsOnlineFeedback.FireUpdate();
			VolumeLevelFeedback.FireUpdate();
			CurrentSourceFeedback.FireUpdate();
			InputNumberFeedback.FireUpdate();
		}
	}
}
