﻿namespace Microsoft.ApplicationInsights.Tests
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector.Implementation;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    ///  PerformanceCounterUtilityTests
    /// </summary>
    [TestClass]
    public class PerformanceCounterUtilityTestsCommon
    {
        // TODO enable Non windows test when CI is configured to run in linux.
        [TestMethod]        
        public void GetCollectorReturnsCorrectCollector()
        {
            try
            {
                var actual = PerformanceCounterUtility.GetPerformanceCollector();
#if NETCOREAPP1_0
                Assert.AreEqual("StandardPerformanceCollectorStub", actual.GetType().Name);
#elif NETCOREAPP2_0
            Assert.AreEqual("StandardPerformanceCollector", actual.GetType().Name);
#else // NET45
            Assert.AreEqual("StandardPerformanceCollector", actual.GetType().Name);
#endif
            }
            finally
            {
                PerformanceCounterUtility.isAzureWebApp = null;
            }
        }

        [TestMethod]
        public void GetCollectorReturnsWebAppCollector()
        {
            try
            {
                Environment.SetEnvironmentVariable("WEBSITE_SITE_NAME", "something");
                var actual = PerformanceCounterUtility.GetPerformanceCollector();
                Assert.AreEqual("WebAppPerformanceCollector", actual.GetType().Name);
            }
            finally
            {
                PerformanceCounterUtility.isAzureWebApp = null;
                Environment.SetEnvironmentVariable("WEBSITE_SITE_NAME", string.Empty);
                Task.Delay(1000).Wait();
            }
        }

        [TestMethod]
        public void GetCollectorReturnsXPlatformCollectorForWebAppForLinux()
        {
#if NETCOREAPP2_0
            var original = PerformanceCounterUtility.IsWindows;
            try
            {
                Environment.SetEnvironmentVariable("WEBSITE_SITE_NAME", "something");                
                PerformanceCounterUtility.IsWindows = false;
                var actual = PerformanceCounterUtility.GetPerformanceCollector();
                Assert.AreEqual("PerformanceCollectorXPlatform", actual.GetType().Name);
            }
            finally
            {
                PerformanceCounterUtility.IsWindows = original;
                PerformanceCounterUtility.isAzureWebApp = null;
                Environment.SetEnvironmentVariable("WEBSITE_SITE_NAME", string.Empty);
                Task.Delay(1000).Wait();                
            }
#endif
        }

        [TestMethod]
        public void GetCollectorReturnsXPlatformCollectorForNonWindows()
        {
#if NETCOREAPP2_0
            var original = PerformanceCounterUtility.IsWindows;
            try
            {                
                PerformanceCounterUtility.IsWindows = false;
                var actual = PerformanceCounterUtility.GetPerformanceCollector();
                Assert.AreEqual("PerformanceCollectorXPlatform", actual.GetType().Name);
            }
            finally
            {
                PerformanceCounterUtility.IsWindows = original;
                PerformanceCounterUtility.isAzureWebApp = null;
            }
#endif
        }
    }
}