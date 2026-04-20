using System;
using System.Globalization;
using System.Text;
using Crestron.SimplSharp.Net.Http;

namespace PepperDash.Essentials.Plugins.Samsung.TizenWebsocket
{
    /// <summary>
    /// UPnP SOAP client for Samsung Tizen display RenderingControl service.
    /// Provides direct volume set/get and mute set/get via SOAP over HTTP.
    /// Samsung Tizen TVs expose RenderingControl on port 9197 by default.
    /// </summary>
    public class SamsungTizenUpnpClient : IDisposable
    {
        private const string RenderingControlUrn = "urn:schemas-upnp-org:service:RenderingControl:1";
        private const string DefaultControlPath = "/upnp/control/RenderingControl1";
        private const int DefaultPort = 9197;
        private const int RequestTimeoutMs = 5000;

        private readonly string baseUrl;
        private readonly HttpClient httpClient;
        private bool isDisposed;

        public event EventHandler<string> OnInfoMessage;
        public event EventHandler<string> OnVerboseMessage;
        public event EventHandler<Exception> OnError;

        /// <summary>
        /// Creates a UPnP client targeting the Samsung RenderingControl service.
        /// </summary>
        /// <param name="hostAddress">IP address or hostname of the Samsung display.</param>
        /// <param name="port">UPnP service port (default 9197).</param>
        public SamsungTizenUpnpClient(string hostAddress, int port = DefaultPort)
        {
            if (string.IsNullOrEmpty(hostAddress))
                throw new ArgumentNullException("hostAddress");

            baseUrl = string.Format("http://{0}:{1}{2}", hostAddress, port, DefaultControlPath);

            httpClient = new HttpClient();
            httpClient.TimeoutEnabled = true;
            httpClient.Timeout = RequestTimeoutMs;
            httpClient.KeepAlive = false;
        }

        #region Volume

        /// <summary>
        /// Sets the display volume to an absolute level (0-100).
        /// </summary>
        /// <returns>True if the SOAP request succeeded.</returns>
        public bool SetVolume(int level)
        {
            var clamped = Math.Max(0, Math.Min(100, level));
            var body = BuildSoapEnvelope("SetVolume",
                "<InstanceID>0</InstanceID>" +
                "<Channel>Master</Channel>" +
                string.Format("<DesiredVolume>{0}</DesiredVolume>", clamped));

            return SendSoapRequest("SetVolume", body);
        }

        /// <summary>
        /// Gets the current volume level from the display (0-100).
        /// </summary>
        /// <returns>Volume level, or -1 on failure.</returns>
        public int GetVolume()
        {
            var body = BuildSoapEnvelope("GetVolume",
                "<InstanceID>0</InstanceID>" +
                "<Channel>Master</Channel>");

            var response = SendSoapRequestWithResponse("GetVolume", body);
            if (response == null) return -1;

            return ParseIntElement(response, "CurrentVolume");
        }

        #endregion

        #region Mute

        /// <summary>
        /// Sets the mute state on the display.
        /// </summary>
        /// <param name="mute">True to mute, false to unmute.</param>
        /// <returns>True if the SOAP request succeeded.</returns>
        public bool SetMute(bool mute)
        {
            var body = BuildSoapEnvelope("SetMute",
                "<InstanceID>0</InstanceID>" +
                "<Channel>Master</Channel>" +
                string.Format("<DesiredMute>{0}</DesiredMute>", mute ? "1" : "0"));

            return SendSoapRequest("SetMute", body);
        }

        /// <summary>
        /// Gets the current mute state from the display.
        /// </summary>
        /// <returns>True if muted, false if unmuted, null on failure.</returns>
        public bool? GetMute()
        {
            var body = BuildSoapEnvelope("GetMute",
                "<InstanceID>0</InstanceID>" +
                "<Channel>Master</Channel>");

            var response = SendSoapRequestWithResponse("GetMute", body);
            if (response == null) return null;

            var value = ParseIntElement(response, "CurrentMute");
            if (value < 0) return null;
            return value != 0;
        }

        #endregion

        #region SOAP Helpers

        private static string BuildSoapEnvelope(string action, string innerXml)
        {
            return string.Format(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body>" +
                "<u:{0} xmlns:u=\"{1}\">{2}</u:{0}>" +
                "</s:Body></s:Envelope>",
                action, RenderingControlUrn, innerXml);
        }

        private bool SendSoapRequest(string action, string body)
        {
            try
            {
                var request = BuildHttpRequest(action, body);
                OnVerboseMessage?.Invoke(this, string.Format("UPnP TX [{0}]: {1}", action, body));

                var response = httpClient.Dispatch(request);

                if (response.Code >= 200 && response.Code < 300)
                {
                    OnVerboseMessage?.Invoke(this, string.Format("UPnP RX [{0}]: {1}", action, response.Code));
                    return true;
                }

                OnError?.Invoke(this, new InvalidOperationException(
                    string.Format("UPnP {0} failed with HTTP {1}: {2}", action, response.Code, response.ContentString)));
                return false;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new InvalidOperationException(
                    string.Format("UPnP {0} request failed: {1}", action, ex.Message), ex));
                return false;
            }
        }

        private string SendSoapRequestWithResponse(string action, string body)
        {
            try
            {
                var request = BuildHttpRequest(action, body);
                OnVerboseMessage?.Invoke(this, string.Format("UPnP TX [{0}]: {1}", action, body));

                var response = httpClient.Dispatch(request);

                if (response.Code >= 200 && response.Code < 300)
                {
                    OnVerboseMessage?.Invoke(this, string.Format("UPnP RX [{0}]: {1} ({2} bytes)",
                        action, response.Code, response.ContentString?.Length ?? 0));
                    return response.ContentString;
                }

                OnError?.Invoke(this, new InvalidOperationException(
                    string.Format("UPnP {0} failed with HTTP {1}: {2}", action, response.Code, response.ContentString)));
                return null;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new InvalidOperationException(
                    string.Format("UPnP {0} request failed: {1}", action, ex.Message), ex));
                return null;
            }
        }

        private HttpClientRequest BuildHttpRequest(string action, string body)
        {
            var request = new HttpClientRequest();
            request.Url.Parse(baseUrl);
            request.RequestType = RequestType.Post;
            request.Header.SetHeaderValue("SOAPAction",
                string.Format("\"{0}#{1}\"", RenderingControlUrn, action));
            request.Header.SetHeaderValue("Content-Type", "text/xml; charset=\"utf-8\"");
            request.ContentString = body;
            return request;
        }

        /// <summary>
        /// Parses an integer value from a simple XML element by tag name.
        /// Uses basic string parsing to avoid System.Xml dependency overhead.
        /// </summary>
        private static int ParseIntElement(string xml, string elementName)
        {
            var openTag = string.Format("<{0}>", elementName);
            var closeTag = string.Format("</{0}>", elementName);

            var startIndex = xml.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
            if (startIndex < 0) return -1;

            startIndex += openTag.Length;
            var endIndex = xml.IndexOf(closeTag, startIndex, StringComparison.OrdinalIgnoreCase);
            if (endIndex < 0) return -1;

            var valueStr = xml.Substring(startIndex, endIndex - startIndex).Trim();

            int result;
            if (int.TryParse(valueStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                return result;

            return -1;
        }

        #endregion

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            try
            {
                httpClient?.Dispose();
            }
            catch
            {
                // Suppress disposal errors
            }
        }
    }
}
