using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;

namespace PepperDash.Essentials.Plugin
{
	/// <summary>
	/// Bridge join map for the Samsung Tizen WebSocket display plugin.
	/// </summary>
	public class SamsungTizenWebsocketBridgeJoinMap : DisplayControllerJoinMap
	{
		#region Digital

		[JoinName("MuteToggle")]
		public JoinDataComplete MuteToggle = new JoinDataComplete(
			new JoinData { JoinNumber = 3, JoinSpan = 1 },
			new JoinMetadata
			{
				Description = "Mute Toggle / Is Muted Feedback",
				JoinCapabilities = eJoinCapabilities.ToFromSIMPL,
				JoinType = eJoinType.Digital
			});

		[JoinName("InputHdmi1")]
		public JoinDataComplete InputHdmi1 = new JoinDataComplete(
			new JoinData { JoinNumber = 11, JoinSpan = 1 },
			new JoinMetadata
			{
				Description = "Select HDMI 1",
				JoinCapabilities = eJoinCapabilities.FromSIMPL,
				JoinType = eJoinType.Digital
			});

		[JoinName("InputHdmi2")]
		public JoinDataComplete InputHdmi2 = new JoinDataComplete(
			new JoinData { JoinNumber = 12, JoinSpan = 1 },
			new JoinMetadata
			{
				Description = "Select HDMI 2",
				JoinCapabilities = eJoinCapabilities.FromSIMPL,
				JoinType = eJoinType.Digital
			});

		[JoinName("InputHdmi3")]
		public JoinDataComplete InputHdmi3 = new JoinDataComplete(
			new JoinData { JoinNumber = 13, JoinSpan = 1 },
			new JoinMetadata
			{
				Description = "Select HDMI 3",
				JoinCapabilities = eJoinCapabilities.FromSIMPL,
				JoinType = eJoinType.Digital
			});

		[JoinName("InputHdmi4")]
		public JoinDataComplete InputHdmi4 = new JoinDataComplete(
			new JoinData { JoinNumber = 14, JoinSpan = 1 },
			new JoinMetadata
			{
				Description = "Select HDMI 4",
				JoinCapabilities = eJoinCapabilities.FromSIMPL,
				JoinType = eJoinType.Digital
			});

		[JoinName("InputDisplayPort")]
		public JoinDataComplete InputDisplayPort = new JoinDataComplete(
			new JoinData { JoinNumber = 15, JoinSpan = 1 },
			new JoinMetadata
			{
				Description = "Select DisplayPort",
				JoinCapabilities = eJoinCapabilities.FromSIMPL,
				JoinType = eJoinType.Digital
			});

		#endregion


		#region Serial

		[JoinName("DeviceName")]
		public JoinDataComplete DeviceName = new JoinDataComplete(
			new JoinData { JoinNumber = 1, JoinSpan = 1 },
			new JoinMetadata
			{
				Description = "Device Name",
				JoinCapabilities = eJoinCapabilities.ToSIMPL,
				JoinType = eJoinType.Serial
			});

		[JoinName("CurrentSource")]
		public JoinDataComplete CurrentSource = new JoinDataComplete(
			new JoinData { JoinNumber = 2, JoinSpan = 1 },
			new JoinMetadata
			{
				Description = "Current Source",
				JoinCapabilities = eJoinCapabilities.ToSIMPL,
				JoinType = eJoinType.Serial
			});

		#endregion


		/// <summary>
		/// Plugin device BridgeJoinMap constructor.
		/// </summary>
		/// <param name="joinStart">Join number offset on the EISC bridge.</param>
		public SamsungTizenWebsocketBridgeJoinMap(uint joinStart)
			: base(joinStart, typeof(SamsungTizenWebsocketBridgeJoinMap))
		{
		}
	}
}
