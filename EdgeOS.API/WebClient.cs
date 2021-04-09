﻿using EdgeOS.API.Types.Configuration;
using EdgeOS.API.Types.REST.Requests;
using EdgeOS.API.Types.REST.Responses;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Web;

namespace EdgeOS.API
{
    /// <summary>Provides an API into EdgeOS based off the official API.</summary>
    public class WebClient : IDisposable
    {
        /// <summary>The EdgeOS SessionID returned after logging in.</summary>
        public string SessionID;

        /// <summary>The EdgeOS Cross-Site Request Forgery (CSRF) token returned after logging in.</summary>
        public string CSRFToken;

        /// <summary>The HTTP Client object that all requests will be performed from. It may have valid credentials pre-configured if <see cref="Login"/> is invoked.</summary>
        private readonly HttpClient _httpClient;

        /// <summary>Creates an instance of the WebClient which can be used to call EdgeOS API methods.</summary>
        /// <param name="host">The EdgeOS hostname this instance will contact.</param>
        public WebClient(string host)
        {
            // Prevent .NET from consuming the HTTP 303 that contains our session tokens.
            _httpClient = new HttpClient(new HttpClientHandler() { AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
            {
                // A EdgeOS API endpoint is the hostname.
                BaseAddress = new Uri(host)
            };

            // Be a good net citizen and reveal who we are.
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "C#-EdgeOS-API");
        }

        /// <summary>Allows a local .crt certificate file to be used to validate a host.</summary>
        public void AllowLocalCertificates()
        {
            // Ignore certificate trust errors if there is a saved public key pinned.
            ServicePointManager.ServerCertificateValidationCallback = ServerCertificateValidationCallback.PinPublicKey;
        }

        /// <summary>Attempt to login to the EdgeOS device and configure the <seealso cref="HttpClient"/> with the session credentials for future usage.</summary>
        /// <param name="username">The username this instance will use to login to the EdgeOS device.</param>
        /// <param name="password">The password this instance will use to login to the EdgeOS device.</param>
        public void Login(string username, string password)
        {
            // Teardown any previous session.
            SessionID = null;
            CSRFToken = null;

            // Build up the HTML Form.
            List<KeyValuePair<string, string>> loginForm = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("username", username),
                new KeyValuePair<string, string>("password", password)
            };

            // Perform the HTTP POST.
            HttpResponseMessage httpResponse = _httpClient.PostAsync("/", new FormUrlEncodedContent(loginForm)).Result;

            // The server does not correctly use HTTP Status codes.
            switch (httpResponse.StatusCode)
            {
                case HttpStatusCode.OK:
                    string response = httpResponse.Content.ReadAsStringAsync().Result;

                    // Check if the login failed (it likely did).
                    if (response.Contains("The username or password you entered is incorrect")) { throw new FormatException("The username or password you entered is incorrect"); }

                    break;
                case HttpStatusCode.SeeOther:
                    // The response headers will contain the session in a cookie if successful.
                    HttpResponseHeaders headers = httpResponse.Headers;

                    // If for whatever reason login fails then a cookie will not be present.
                    if (!(headers.Contains("Set-Cookie"))) { throw new FormatException("Expected session cookie headers were not present in the response back from the server."); }

                    // The stats connection requires the session ID for authentication.
                    const string sessionNeedle = "PHPSESSID=";

                    // The X-CSRF-TOKEN is used to validate sensitive HTTP POSTs.
                    const string csrfNeedle = "X-CSRF-TOKEN=";

                    foreach (string cookie in headers.GetValues("Set-Cookie"))
                    {
                        // We are only interested in the PHPSESSID and X-CSRF-TOKEN.
                        if (cookie.StartsWith(sessionNeedle))
                        {
                            int semicolon = cookie.IndexOf(';');
                            SessionID = semicolon == -1 ? cookie.Substring(sessionNeedle.Length) : cookie.Substring(sessionNeedle.Length, semicolon - sessionNeedle.Length);
                        }
                        else if (cookie.StartsWith(csrfNeedle))
                        {
                            int semicolon = cookie.IndexOf(';');
                            CSRFToken = semicolon == -1 ? cookie.Substring(csrfNeedle.Length) : cookie.Substring(csrfNeedle.Length, semicolon - sessionNeedle.Length);
                        }

                        // Do we have everything to break out of the loop?
                        if (SessionID != null && CSRFToken != null) { break; }
                    }

                    // There's a chance the authentication has changed and we are no longer reliant on a PHPSESSID.
                    if (SessionID == null) { throw new FormatException("Unable to find session credentials."); }

                    break;
            }
        }

        /// <summary>Log out of the EdgeOS device.</summary>
        public void Logout()
        {
            _ = _httpClient.GetAsync("/logout").Result;
        }

        #region Edge - General

        /// <summary>Attempt to authenticate with the EdgeOS device and will internally create a session but will not return session tokens to allow further requests. See <see cref="Login"/> to actually login to obtain a session.</summary>
        /// <returns>The response from the device.</returns>
        public AuthenticateResponse Authenticate(string username, string password)
        {
            // Build up the HTML Form.
            List<KeyValuePair<string, string>> loginForm = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("username", username),
                new KeyValuePair<string, string>("password", password)
            };

            // Perform the HTTP POST.
            HttpResponseMessage httpResponse = _httpClient.PostAsync("/api/edge/auth.json", new FormUrlEncodedContent(loginForm)).Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a AuthenticateResponse.
                return JsonConvert.DeserializeObject<AuthenticateResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        /// <summary>Make a batch query/deletion/update request to specific parts of the device’s configuration.</summary>
        /// <param name="batchRequest">An object containing DELETE/SET/GET operations to perform.</param>
        /// <returns>The response from the device.</returns>
        public ConfigurationSettingsBatchResponse ConfigurationSettingsBatch(ConfigurationSettingsBatchRequest batchRequest)
        {
            // Serialize our concrete class into a JSON String.
            string requestContent = JsonConvert.SerializeObject(batchRequest, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });

            // We build up our request.
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/edge/batch.json") { Content = new StringContent(requestContent, Encoding.UTF8, "application/json") };

            // This end-point is protected with a Cross-Site Request Forgery (CSRF) token.
            httpRequest.Headers.Add("X-CSRF-TOKEN", CSRFToken);

            // Send it to the Configuration Settings Batch end-point with the appropriate CSRF header.
            HttpResponseMessage httpResponse = _httpClient.SendAsync(httpRequest).Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a ConfigurationSettingsBatchResponse.
                return JsonConvert.DeserializeObject<ConfigurationSettingsBatchResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        /// <summary>Delete specific parts of the device’s configuration.</summary>
        /// <param name="deleteRequest">The Configuration options to delete from the configuration.</param>
        /// <returns>The response from the device.</returns>
        public ConfigurationSettingsDeleteResponse ConfigurationSettingsDelete(Configuration deleteRequest)
        {
            // Serialize our concrete class into a JSON String.
            string requestContent = JsonConvert.SerializeObject(deleteRequest, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });

            // We build up our request.
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/edge/delete.json") { Content = new StringContent(requestContent, Encoding.UTF8, "application/json") };

            // This end-point is protected with a Cross-Site Request Forgery (CSRF) token.
            httpRequest.Headers.Add("X-CSRF-TOKEN", CSRFToken);

            // Send it to the Configuration Settings Delete end-point with the appropriate CSRF header.
            HttpResponseMessage httpResponse = _httpClient.SendAsync(httpRequest).Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a ConfigurationSettingsDeleteResponse.
                return JsonConvert.DeserializeObject<ConfigurationSettingsDeleteResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        /// <summary>Get a predefined part of the device's configuration.</summary>
        /// <returns>The response from the device.</returns>
        public ConfigurationSettingsGetResponse ConfigurationSettingsGetPredefinedList()
        {
            // Send it to the Configuration Settings Get Predefined List end-point.
            HttpResponseMessage httpResponse = _httpClient.GetAsync("/api/edge/get.json").Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a ConfigurationSettingsGetResponse.
                return JsonConvert.DeserializeObject<ConfigurationSettingsGetResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        /// <summary>Get specific part(s) of the device's configuration.</summary>
        /// <returns>The response from the device.</returns>
        public ConfigurationSettingsGetResponse ConfigurationSettingsGetSections(string requestContent)
        {
            // Send it to the Configuration Settings Get Partial end-point.
            HttpResponseMessage httpResponse = _httpClient.GetAsync("/api/edge/partial.json?struct=" + HttpUtility.UrlEncode(requestContent)).Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a ConfigurationSettingsGetResponse.
                return JsonConvert.DeserializeObject<ConfigurationSettingsGetResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        /// <summary>Get specific tree part(s) of the device's configuration.</summary>
        /// <param name="requestPath">The JSON key(s) to filter on, such as ["firewall", "group", "address-group"].</param>
        /// <returns>The response from the device.</returns>
        public ConfigurationSettingsGetTreeResponse ConfigurationSettingsGetTree(string[] requestPath)
        {
            // Build the querystring.
            StringBuilder queryString = new StringBuilder();
            for (int count = 0; count < requestPath.Length; count++)
            {
                if (count > 0) { queryString.Append('&'); }
                queryString.Append("node[]=" + HttpUtility.UrlEncode(requestPath[count]));
            }

            // Send it to the Configuration Settings Get Tree end-point.
            HttpResponseMessage httpResponse = _httpClient.GetAsync("/api/edge/getcfg.json" + (requestPath.Length > 0 ? "?" + queryString.ToString() : null)).Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a ConfigurationSettingsGetTreeResponse.
                return JsonConvert.DeserializeObject<ConfigurationSettingsGetTreeResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        /// <summary>Set specific parts of the device’s configuration.</summary>
        /// <param name="setRequest">The Configuration options to set in the configuration.</param>
        /// <returns>The response from the device.</returns>
        public ConfigurationSettingsSetResponse ConfigurationSettingsSet(Configuration setRequest)
        {
            // Serialize our concrete class into a JSON String.
            string requestContent = JsonConvert.SerializeObject(setRequest, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });

            // We build up our request.
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/edge/set.json") { Content = new StringContent(requestContent, Encoding.UTF8, "application/json") };

            // This end-point is protected with a Cross-Site Request Forgery (CSRF) token.
            httpRequest.Headers.Add("X-CSRF-TOKEN", CSRFToken);

            // Send it to the Configuration Settings Set end-point with the appropriate CSRF header.
            HttpResponseMessage httpResponse = _httpClient.SendAsync(httpRequest).Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a ConfigurationSettingsSetResponse.
                return JsonConvert.DeserializeObject<ConfigurationSettingsSetResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        //TODO: Data methods.

        /// <summary>Attempt to keep the session alive on the EdgeOS device.</summary>
        public void Heartbeat()
        {
            _httpClient.GetAsync("/api/edge/heartbeat.json?_=" + (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
        }

        //TODO: Upgrade Firmware method.

        //TODO: Wizard Feature method.

        //TODO: Wizard Setup method.

        #endregion

        #region Edge - Configuration

        /// <summary>Save the device's entire configuration to a temporary file on the disk in preparation to download it (see <see cref="ConfigurationDownload"/>).</summary>
        /// <returns>The response from the device.</returns>
        public ConfigurationDownloadPrepareResponse ConfigurationDownloadPrepare()
        {
            // Send it to the Download Configuration end-point.
            HttpResponseMessage httpResponse = _httpClient.GetAsync("/api/edge/config/save.json").Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a ConfigurationDownloadPrepareResponse.
                return JsonConvert.DeserializeObject<ConfigurationDownloadPrepareResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        /// <summary>Get the device's entire configuration from the temporary file on disk that it was saved into (see <see cref="ConfigurationDownloadPrepare"/>.</summary>
        /// <returns>The response from the device.</returns>
        public Stream ConfigurationDownload()
        {
            // Send it to the Download Configuration end-point.
            HttpResponseMessage httpResponse = _httpClient.GetAsync("/files/config/").Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                // Return the stream.
                return httpResponse.Content.ReadAsStreamAsync().Result;
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        //TODO: Restore Configuration method.

        #endregion

        #region Edge - Optical Network Unit (ONU)

        //TODO: ONU Upgrade method.

        //TODO: ONU Reboot method.

        #endregion

        #region Edge - Operations

        /// <summary>Check for firmware updates for the device.</summary>
        /// <returns>The response from the device.</returns>
        public OperationResponse OperationCheckForFirmwareUpdates()
        {
            // We build up our request.
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/edge/operation/refresh-fw-latest-status.json");

            // This end-point is protected with a Cross-Site Request Forgery (CSRF) token.
            httpRequest.Headers.Add("X-CSRF-TOKEN", CSRFToken);

            // Send it to the Operation Check For Firmware Updates end-point with the appropriate CSRF header.
            HttpResponseMessage httpResponse = _httpClient.SendAsync(httpRequest).Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a OperationResponse.
                return JsonConvert.DeserializeObject<OperationResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        /// <summary>Clear Traffic Analysis data.</summary>
        /// <returns>The response from the device.</returns>
        public OperationResponse OperationClearTrafficAnalysis()
        {
            // We build up our request.
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/edge/operation/clear-traffic-analysis.json");

            // This end-point is protected with a Cross-Site Request Forgery (CSRF) token.
            httpRequest.Headers.Add("X-CSRF-TOKEN", CSRFToken);

            // Send it to the Operation Clear Traffic Analysis end-point with the appropriate CSRF header.
            HttpResponseMessage httpResponse = _httpClient.SendAsync(httpRequest).Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a OperationResponse.
                return JsonConvert.DeserializeObject<OperationResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        /// <summary>Reset the device back to its factory-default state (erasing all user-generated files and deleting backup firmware image).</summary>
        /// <returns>The response from the device.</returns>
        public OperationResponse OperationFactoryReset()
        {
            // We build up our request.
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/edge/operation/factory-reset.json");

            // This end-point is protected with a Cross-Site Request Forgery (CSRF) token.
            httpRequest.Headers.Add("X-CSRF-TOKEN", CSRFToken);

            // Send it to the Operation Factory Reset end-point with the appropriate CSRF header.
            HttpResponseMessage httpResponse = _httpClient.SendAsync(httpRequest).Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a OperationResponse.
                return JsonConvert.DeserializeObject<OperationResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        //TODO: Generate Support File methods.

        /// <summary>Reboot the device.</summary>
        /// <returns>The response from the device.</returns>
        public OperationResponse OperationReboot()
        {
            // We build up our request.
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/edge/operation/shutdown.json");

            // This end-point is protected with a Cross-Site Request Forgery (CSRF) token.
            httpRequest.Headers.Add("X-CSRF-TOKEN", CSRFToken);

            // Send it to the Operation Reboot end-point with the appropriate CSRF header.
            HttpResponseMessage httpResponse = _httpClient.SendAsync(httpRequest).Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a OperationResponse.
                return JsonConvert.DeserializeObject<OperationResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        /// <summary>Release the DHCP Lease for a specific interface (defective in most EdgeRouter firmware).</summary>
        /// <param name="interface">The specific interface to request to release the DHCP lease (e.g. eth0).</param>
        /// <returns>The response from the device.</returns>
        public OperationResponse OperationReleaseDHCP(string @interface)
        {
            // Build up the HTML Form.
            List<KeyValuePair<string, string>> releaseDHCPform = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("interface", @interface)
            };

            // We build up our request.
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/edge/operation/release-dhcp.json") { Content = new FormUrlEncodedContent(releaseDHCPform) };

            // This end-point is protected with a Cross-Site Request Forgery (CSRF) token.
            httpRequest.Headers.Add("X-CSRF-TOKEN", CSRFToken);

            // Send it to the Operation Release DHCP end-point with the appropriate CSRF header.
            HttpResponseMessage httpResponse = _httpClient.SendAsync(httpRequest).Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a OperationResponse.
                return JsonConvert.DeserializeObject<OperationResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        /// <summary>Renew the DHCP Lease for a specific interface (defective in most EdgeRouter firmware).</summary>
        /// <param name="interface">The specific interface to request to renew the DHCP lease (e.g. eth0).</param>
        /// <returns>The response from the device.</returns>
        public OperationResponse OperationRenewDHCP(string @interface)
        {
            // Build up the HTML Form.
            List<KeyValuePair<string, string>> renewDHCPform = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("interface", @interface)
            };

            // We build up our request.
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/edge/operation/renew-dhcp.json") { Content = new FormUrlEncodedContent(renewDHCPform) };

            // This end-point is protected with a Cross-Site Request Forgery (CSRF) token.
            httpRequest.Headers.Add("X-CSRF-TOKEN", CSRFToken);

            // Send it to the Operation Renew DHCP end-point with the appropriate CSRF header.
            HttpResponseMessage httpResponse = _httpClient.SendAsync(httpRequest).Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a OperationResponse.
                return JsonConvert.DeserializeObject<OperationResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        /// <summary>Reset the device configuration back to default values (backup firmware image and user-generated files will remain intact).</summary>
        /// <returns>The response from the device.</returns>
        public OperationResponse OperationResetDefaultConfiguration()
        {
            // We build up our request.
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/edge/operation/reset-default-config.json");

            // This end-point is protected with a Cross-Site Request Forgery (CSRF) token.
            httpRequest.Headers.Add("X-CSRF-TOKEN", CSRFToken);

            // Send it to the Operation Reset Default Configuration end-point with the appropriate CSRF header.
            HttpResponseMessage httpResponse = _httpClient.SendAsync(httpRequest).Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a OperationResponse.
                return JsonConvert.DeserializeObject<OperationResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        /// <summary>Shutdown the device.</summary>
        /// <returns>The response from the device.</returns>
        public OperationResponse OperationShutdown()
        {
            // We build up our request.
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/edge/operation/shutdown.json");

            // This end-point is protected with a Cross-Site Request Forgery (CSRF) token.
            httpRequest.Headers.Add("X-CSRF-TOKEN", CSRFToken);

            // Send it to the Operation Shutdown end-point with the appropriate CSRF header.
            HttpResponseMessage httpResponse = _httpClient.SendAsync(httpRequest).Result;

            // Check the result is what we are expecting (and throw an exception if not).
            httpResponse.EnsureSuccessStatusCode();

            // If the response contains content we want to read it.
            if (httpResponse.Content != null)
            {
                string responseContent = httpResponse.Content.ReadAsStringAsync().Result;

                // Deserialize the responseContent to a OperationResponse.
                return JsonConvert.DeserializeObject<OperationResponse>(responseContent);
            }
            else
            {
                // No content returned.
                return null;
            }
        }

        #endregion

        #region Optical Line Terminal (OLT) - General

        //TODO: OLT Get Connected ONU method.

        #endregion

        #region Optical Line Terminal (OLT) - Optical Network Unit (ONU)

        //TODO: OLT Generate ONU Support methods.

        //TODO: OLT Get Connected WiFi Clients method.

        //TODO: OLT Locate ONU method.

        //TODO: OLT Reset ONU method.

        #endregion

        #region Wizards

        //TODO: Wizards List All Wizards method.

        //TODO: Wizards Specific Wizard Crete method.

        //TODO: Wizards Specific Wizard Download method.

        //TODO: Wizards Specific Wizard Remove method.

        //TODO: Wizards Specific Wizard Upload method.

        #endregion

        /// <summary>Ensures proper clean up of the resources.</summary>
        public void Dispose()
        {
            if (_httpClient != null)
            {

                // Attempt to log out.
                if (SessionID != null) { Logout(); }

                // Dispose of the _httpClient field.
                _httpClient.Dispose();
            }
        }
    }
}