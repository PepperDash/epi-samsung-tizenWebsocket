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

		#endregion


		#region Analog

		#endregion


		#region Serial


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
