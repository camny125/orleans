﻿/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.WindowsAzure.ServiceRuntime;

using Orleans.Runtime.Configuration;


namespace Orleans.Runtime.Host
{
    /// <summary>
    /// Utility class for initializing an Orleans client running inside Azure.
    /// </summary>
    public static class AzureClient
    {
        /// <summary>Number of retry attempts to make when searching for gateway silos to connect to.</summary>
        public static readonly int MaxRetries = AzureConstants.MAX_RETRIES;  // 120 x 5s = Total: 10 minutes
        /// <summary>Amount of time to pause before each retry attempt.</summary>
        public static readonly TimeSpan StartupRetryPause = AzureConstants.STARTUP_TIME_PAUSE; // 5 seconds

        /// <summary>
        /// Whether the Orleans Azure client runtime has already been initialized
        /// </summary>
        /// <returns><c>true</c> if client runtime is already initialized</returns>
        public static bool IsInitialized { get { return GrainClient.IsInitialized; } }

        /// <summary>
        /// Initialise the Orleans client runtime in this Azure process
        /// </summary>
        public static void Initialize()
        {
            InitializeImpl_FromFile(null);
        }

        /// <summary>
        /// Initialise the Orleans client runtime in this Azure process
        /// </summary>
        /// <param name="orleansClientConfigFile">Location of the Orleans client config file to use for base config settings</param>
        /// <remarks>Any silo gateway address specified in the config file is ignored, and gateway endpoint info is read from the silo instance table in Azure storage instead.</remarks>
        public static void Initialize(FileInfo orleansClientConfigFile)
        {
            InitializeImpl_FromFile(orleansClientConfigFile);
        }

        /// <summary>
        /// Initialise the Orleans client runtime in this Azure process
        /// </summary>
        /// <param name="clientConfigFilePath">Location of the Orleans client config file to use for base config settings</param>
        /// <remarks>Any silo gateway address specified in the config file is ignored, and gateway endpoint info is read from the silo instance table in Azure storage instead.</remarks>
        public static void Initialize(string clientConfigFilePath)
        {
            InitializeImpl_FromFile(new FileInfo(clientConfigFilePath));
        }

        /// <summary>
        /// Initializes the Orleans client runtime in this Azure process from the provided client configuration object. 
        /// If the configuration object is null, the initialization fails. 
        /// </summary>
        /// <param name="config">A ClientConfiguration object.</param>
        public static void Initialize(ClientConfiguration config)
        {
            InitializeImpl_FromConfig(config);
        }

        /// <summary>
        /// Uninitializes the Orleans client runtime in this Azure process. 
        /// </summary>
        public static void Uninitialize()
        {
            if (!GrainClient.IsInitialized) return;

            Trace.TraceInformation("Uninitializing connection to Orleans gateway silo.");
            GrainClient.Uninitialize();
        }

        #region Internal implementation of client initialization processing

        private static void InitializeImpl_FromFile(FileInfo configFile)
        {
            if (GrainClient.IsInitialized)
            {
                Trace.TraceInformation("Connection to Orleans gateway silo already initialized.");
                return;
            }

            ClientConfiguration config;
            try
            {
                if (configFile == null)
                {
                    Trace.TraceInformation("Looking for standard Orleans client config file");
                    config = ClientConfiguration.StandardLoad();
                }
                else
                {
                    var configFileLocation = configFile.FullName;
                    Trace.TraceInformation("Loading Orleans client config file {0}", configFileLocation);
                    config = ClientConfiguration.LoadFromFile(configFileLocation);
                }
            }
            catch (Exception ex)
            {
                var msg = String.Format("Error loading Orleans client configuration file {0} {1} -- unable to continue. {2}", configFile, ex.Message, TraceLogger.PrintException(ex));
                Trace.TraceError(msg);
                throw new AggregateException(msg, ex);
            }

            Trace.TraceInformation("Overriding Orleans client config from Azure runtime environment.");
            try
            {
                config.DeploymentId = GetDeploymentId();
                config.DataConnectionString = GetDataConnectionString();
                config.GatewayProvider = ClientConfiguration.GatewayProviderType.AzureTable;
            }
            catch (RoleEnvironmentException ex)
            {
                var msg = string.Format("ERROR: No AzureClient role setting value '{0}' specified for this role -- unable to continue", AzureConstants.DataConnectionConfigurationSettingName);
                Trace.TraceError(msg);
                throw new AggregateException(msg, ex);
            }

            InitializeImpl_FromConfig(config);
        }

        private static string GetDeploymentId()
        {
            return GrainClient.TestOnlyNoConnect ? "FakeDeploymentId" : RoleEnvironment.DeploymentId;
        }

        private static string GetDataConnectionString()
        {
            return GrainClient.TestOnlyNoConnect
                ? "FakeConnectionString"
                : RoleEnvironment.GetConfigurationSettingValue(AzureConstants.DataConnectionConfigurationSettingName);
        }

        private static void InitializeImpl_FromConfig(ClientConfiguration config)
        {
            if (GrainClient.IsInitialized)
            {
                Trace.TraceInformation("Connection to Orleans gateway silo already initialized.");
                return;
            }

            //// Find endpoint info for the gateway to this Orleans silo cluster
            //Trace.WriteLine("Searching for Orleans gateway silo via Orleans instance table...");
            var deploymentId = config.DeploymentId;
            var connectionString = config.DataConnectionString;
            if (String.IsNullOrEmpty(deploymentId))
                throw new ArgumentException("Cannot connect to Azure silos with null deploymentId", "config.DeploymentId");
            
            if (String.IsNullOrEmpty(connectionString))
                throw new ArgumentException("Cannot connect to Azure silos with null connectionString", "config.DataConnectionString");
            
            bool initSucceeded = false;
            Exception lastException = null;
            for (int i = 0; i < MaxRetries; i++)
            {
                try
                {
                    // Initialize will throw if cannot find Gateways
                    GrainClient.Initialize(config);
                    initSucceeded = true;
                    break;
                }
                catch (Exception exc) 
                {
                    lastException = exc;
                    Trace.TraceError("Client.Initialize failed with exc -- {0}. Will try again", exc.Message);
                }
                // Pause to let Primary silo start up and register
                Trace.TraceInformation("Pausing {0} awaiting silo and gateways registration for Deployment={1}", StartupRetryPause, deploymentId);
                Thread.Sleep(StartupRetryPause);
            }
            
            if (initSucceeded) return;

            OrleansException err;
            err = lastException != null ? new OrleansException(String.Format("Could not Initialize Client for DeploymentId={0}. Last exception={1}",
                deploymentId, lastException.Message), lastException) : new OrleansException(String.Format("Could not Initialize Client for DeploymentId={0}.", deploymentId));
            Trace.TraceError("Error starting Orleans Azure client application -- {0} -- bailing. {1}", err.Message, TraceLogger.PrintException(err));
            throw err;
        }

        #endregion
    }
}