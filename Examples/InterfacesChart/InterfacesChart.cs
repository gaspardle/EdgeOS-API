﻿using EdgeOS.API;
using EdgeOS.API.Types.Subscription.Requests;
using EdgeOS.API.Types.Subscription.Responses;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace InterfacesChart
{
    public partial class InterfacesChart : Form
    {
        private IConfiguration Configuration;

        // EdgeOS requires logins and session heartbeats to be sent via the REST API.
        private EdgeOSApiClient webClient;

        // EdgeOS requires the session to be renewed or it will expire (we renew every 30s)
        private readonly System.Timers.Timer sessionHeartbeatTimer = new System.Timers.Timer(30000);

        // This holds the StatsConnection for the whole form.
        private StatsConnection statsConnection;

        // How many eth0, eth1 etc. interfaces the EdgeOS device has (this only impacts colouring).
        private const byte NumberOfEthInterfaces = 4;

        // The official EdgeOS colours (as scraped from the Javascript).
        private readonly Color[] paletteColors = new Color[] {
            // #BD1550
            Color.FromArgb(189, 21, 80),
            
            // #E97F02
            Color.FromArgb(233, 127, 2),

            // #FFCC00
            Color.FromArgb(255, 204, 0),

            // #B0C135
            Color.FromArgb(176, 193, 53),

            // #1693A7
            Color.FromArgb(22, 147, 167),

            // #7930AA
            Color.FromArgb(121, 48, 170),

            // #A3A3A3
            Color.FromArgb(163, 163, 163),

            // #A37434
            Color.FromArgb(163, 116, 52)
            };

        /// <summary>The main method for the Windows Forms application.</summary>
        public InterfacesChart()
        {
            InitializeComponent();
        }

        /// <summary>Method which dynamically adds some series of data to the chart and connects to the EdgeOS device once the form is ready.</summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">THe <see cref="EventArgs"/> instance containing the event data.</param>
        private void FormBandwidthChart_Load(object sender, EventArgs e)
        {
            Configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
            var config = Configuration.GetSection("EdgeOSApiCredentials");

            // Check the credentials are provided in the application's configuration file.
            if (config["Username"] == null || config["Password"] == null || config["Host"] == null)
            {
                MessageBox.Show("Program cannot start, some credentials were missing in the program's configuration file.", "Missing Credentials", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }

            // We want to control the ordering of some of the series so we can control the colours.
            for (int count = 0; count < NumberOfEthInterfaces; count++)
            {
                bandwidthChart.Series.Add(new Series("eth" + count + "Rx") { ChartArea = "ChartAreaRx", ChartType = SeriesChartType.StackedColumn, Color = paletteColors[count] });
                bandwidthChart.Series.Add(new Series("eth" + count + "Tx") { ChartArea = "ChartAreaTx", ChartType = SeriesChartType.StackedColumn, Color = paletteColors[count] });
            }

            // This method will be invoked each time the timer has elapsed.
            sessionHeartbeatTimer.Elapsed += (s, a) => webClient.Heartbeat();

            // The WebClient allows us to get a valid SessionID to then use with the StatsConnection.
            webClient = new EdgeOSApiClient("https://" + config["Host"] + "/");

            // Ignore TLS certificate errors if there is a ".crt" file present that matches this host.
            webClient.AllowLocalCertificates();

            // Login to the Router.
            webClient.Login(config["Username"], config["Password"]);

            // Share a valid SessionID with a new StatsConnection object.
            statsConnection = new StatsConnection(webClient.SessionID);

            // Ignore TLS certificate errors if there is a ".crt" file present that matches this host.
            statsConnection.AllowLocalCertificates();
            
            // Setup an event handler for when data is received.
            statsConnection.DataReceived += (object s, SubscriptionDataEvent sde) => { this.Invoke(new Action<object, SubscriptionDataEvent>(Connection_DataReceived), new[] { s, sde }); };

            // Setup an event handler for when the connection state changes.
            statsConnection.ConnectionStatusChanged += Connection_ConnectionStatusChanged;

            var thread = new Thread(StatsThreadStart)
            {
                IsBackground = true,
            };
            thread.Start();
        }

        private void StatsThreadStart()
        {
            statsConnection.ConnectAsync(new Uri("wss://" + Configuration.GetValue<string>("Host") + "/ws/stats"));
        }

        /// <summary>Method which when a StatsConnection is established requests Interface statistics.</summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="connectionStatus">The <see cref="StatsConnection"/>'s new <see cref="StatsConnection.ConnectionStatus"/>.</param>
        private void Connection_ConnectionStatusChanged(object sender, StatsConnection.ConnectionStatus connectionStatus)
        {
            // The sender should be a StatsConnection so that we can interact with that StatsConnection instance.
            StatsConnection statsConnection = sender as StatsConnection;
            if (statsConnection == null) { return; }

            // Specifically what did the ConnectionStatus change to?
            switch (connectionStatus)
            {
                // It was previously not connected and now it is.
                case StatsConnection.ConnectionStatus.Connected:

                    // Compose a subscription request message.
                    SubscriptionRequest subscriptionRequest = new SubscriptionRequest
                    {
                        Subscribe = new Subscription[] { new Subscription() { name = SubscriptionMessageType.Interfaces } },
                        SessionID = statsConnection.SessionID
                    };

                    // Ask for events to be delivered.
                    statsConnection.SubscribeForEvents(subscriptionRequest);

                    // Start the heartbeat timer.
                    sessionHeartbeatTimer.Enabled = true;

                    break;
                // The router has disconnected (usually due to session expiry due to lack of heartbeats).
                case StatsConnection.ConnectionStatus.DisconnectedByHost:

                    // Stop the heartbeat timer.
                    sessionHeartbeatTimer.Enabled = false;

                    // End the program.
                    Close();

                    break;
            }
        }

        /// <summary>Method which is invoked when new <see cref="SubscriptionDataEvent"/> arrives.</summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="SubscriptionDataEvent"/> instance containing the event data.</param>
        private void Connection_DataReceived(object sender, SubscriptionDataEvent e)
        {
            // Ignore any data that isn't an Interfaces response message.
            if (e.rootObject.GetType() != typeof(InterfacesResponse)) { return; }

            InterfacesResponse interfacesRoot = (InterfacesResponse)e.rootObject;

            if (interfacesRoot.Interfaces != null)
            {
                byte manuallyAddedSeriesCount = 0;
                foreach (KeyValuePair<string, InterfacesResponse.Interface> currentInterface in interfacesRoot.Interfaces)
                {
                    // We only care about "eth" devices.
                    if (!currentInterface.Key.StartsWith("eth")) { continue; }

                    string currentRx = currentInterface.Key + "Rx";
                    string currentTx = currentInterface.Key + "Tx";

                    // If the bandwidthChart does not have this series already addded then we will need to add it.
                    if (bandwidthChart.Series.IndexOf(currentRx) == -1)
                    {
                        Series currentRxSeries = new Series(currentRx) { ChartArea = "ChartAreaRx", ChartType = SeriesChartType.StackedColumn };
                        if (manuallyAddedSeriesCount < paletteColors.Length) { currentRxSeries.Color = paletteColors[manuallyAddedSeriesCount]; }
                        bandwidthChart.Series.Add(currentRxSeries);

                        Series currentTxSeries = new Series(currentTx) { ChartArea = "ChartAreaTx", ChartType = SeriesChartType.StackedColumn };
                        if (manuallyAddedSeriesCount < paletteColors.Length) { currentTxSeries.Color = paletteColors[manuallyAddedSeriesCount++]; }
                        bandwidthChart.Series.Add(new Series(currentTx));
                    }

                    bandwidthChart.Series[currentRx].Points.AddY(interfacesRoot.Interfaces[currentInterface.Key].stats.rx_bps);
                    bandwidthChart.Series[currentTx].Points.AddY(interfacesRoot.Interfaces[currentInterface.Key].stats.tx_bps);
                }

                // Adjust Y & X axis scale
                bandwidthChart.ResetAutoValues();

                // We set a limit for the maximum number of points we want to see in the chart.
                const byte numberOfPointsInChart = 50;

                // Check each of the series.
                foreach (Series currentSeries in bandwidthChart.Series)
                {
                    // Keep a constant number of points by removing them from the left
                    while (currentSeries.Points.Count > numberOfPointsInChart)
                    {
                        // Remove data points on the left side
                        while (currentSeries.Points.Count > numberOfPointsInChart) { currentSeries.Points.RemoveAt(0); }
                    }
                }

                // Invalidate chart
                bandwidthChart.Invalidate();
            }
        }

        /// <summary>Clean up any resources being used.</summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (components != null) { components.Dispose(); }

                // Dispose of the statsConnection.
                if (statsConnection != null) { statsConnection.Dispose(); }

                // Dispose the sessionHeartbeatTimer and webClient.
                if (sessionHeartbeatTimer != null) { sessionHeartbeatTimer.Dispose(); }
                if (webClient != null) { webClient.Dispose(); }
            }
            base.Dispose(disposing);
        }

    }

    
}