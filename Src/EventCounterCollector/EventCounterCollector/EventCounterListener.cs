﻿namespace Microsoft.ApplicationInsights.Extensibility.EventCounterCollector.Implementation
{
    using System;
    using System.Collections;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics.Tracing;
    using System.Globalization;
    using System.Text;
    using Microsoft.ApplicationInsights.DataContracts;

    /// <summary>
    /// Implementation to listen to EventCounters.
    /// </summary>
    internal class EventCounterListener : EventListener
    {        
        private readonly string refreshIntervalInSecs;
        private readonly double refreshInternalInSecDouble;
        private readonly EventLevel level = EventLevel.Critical;
        private bool isInitialized = false;
        private TelemetryClient telemetryClient;
        private Dictionary<string, string> refreshIntervalDictionary;

        // Thread-safe variable to hold the list of all EventSourcesCreated.
        // This class may not be instantiated at the time of EventSource creation, so the list of EventSources should be stored to be enabled after initialization.
        private ConcurrentQueue<EventSource> allEventSourcesCreated;        

        // EventSourceNames from which counters are to be collected are the keys for this IDictionary.
        // The value will be the corresponding ICollection of counter names.
        private IDictionary<string, ICollection<string>> countersToCollect = new Dictionary<string, ICollection<string>>();

        public EventCounterListener(TelemetryClient telemetryClient, IList<EventCounterCollectionRequest> eventCounterCollectionRequests, double refreshIntervalSecs)
        {
            try
            {
                this.refreshInternalInSecDouble = refreshIntervalSecs;
                this.refreshIntervalInSecs = refreshIntervalSecs.ToString(CultureInfo.InvariantCulture);
                refreshIntervalDictionary = new Dictionary<string, string>();                
                refreshIntervalDictionary.Add("EventCounterIntervalSec", this.refreshIntervalInSecs);

                this.telemetryClient = telemetryClient;

                foreach (var collectionRequest in eventCounterCollectionRequests)
                {
                    if (!this.countersToCollect.ContainsKey(collectionRequest.EventSourceName))
                    {
                        this.countersToCollect.Add(collectionRequest.EventSourceName, new HashSet<string>() { collectionRequest.EventCounterName });
                    }
                    else
                    {
                        this.countersToCollect[collectionRequest.EventSourceName].Add(collectionRequest.EventCounterName);
                    }
                }

                EventCounterCollectorEventSource.Log.EventCounterInitializeSuccess();
                this.isInitialized = true;

                // Go over every EventSource created before we finished initialization, and enable if required.
                // This will take care of all EventSources created before initialization was done.
                foreach (var eventSource in this.allEventSourcesCreated)
                {
                    this.EnableIfRequired(eventSource);
                }
            }
            catch (Exception ex)
            {
                EventCounterCollectorEventSource.Log.EventCounterCollectorError("EventCounterListener Constructor", ex.Message);
            }
        }

        /// <summary>
        /// Processes notifications about new EventSource creation.
        /// </summary>
        /// <param name="eventSource">EventSource instance.</param>
        /// <remarks>When an instance of an EventCounterListener is created, it will immediately receive notifications about all EventSources already existing in the AppDomain.
        /// Then, as new EventSources are created, the EventListener will receive notifications about them.</remarks>
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            // Keeping track of all EventSources here, as this call may happen before initialization.
            lock (this)
            {
                if (this.allEventSourcesCreated == null)
                {
                    this.allEventSourcesCreated = new ConcurrentQueue<EventSource>();
                }

                this.allEventSourcesCreated.Enqueue(eventSource);
            }

            // If initialization is already done, we can enable EventSource right away.
            // This will take care of all EventSources created after initialization is done.
            if (this.isInitialized)
            {
                this.EnableIfRequired(eventSource);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            // Ignore events if initialization not done yet. We may lose the 1st event if it happens before initialization, in multi-thread situations.
            // Since these are counters, losing the 1st event will not have noticeable impact.
            if (this.isInitialized)
            {
                if (this.countersToCollect.ContainsKey(eventData.EventSource.Name))
                {
                    IDictionary<string, object> eventPayload = eventData.Payload[0] as IDictionary<string, object>;
                    if (eventPayload != null)
                    {
                        this.ExtractAndPostMetric(eventData.EventSource.Name, eventPayload);
                    }
                    else
                    {
                        EventCounterCollectorEventSource.Log.IgnoreEventWrittenAsEventPayloadNotParseable(eventData.EventSource.Name);
                    }
                }
                else
                {
                    EventCounterCollectorEventSource.Log.IgnoreEventWrittenAsEventSourceNotInConfiguredList(eventData.EventSource.Name);
                }
            }
            else
            {
                EventCounterCollectorEventSource.Log.IgnoreEventWrittenAsNotInitialized(eventData.EventSource.Name);
            }
        }

        private void EnableIfRequired(EventSource eventSource)
        {
            try
            {
                // The EventSourceName is in the list we want to collect some counters from.
                if (this.countersToCollect.ContainsKey(eventSource.Name))
                {                    
                    // Unlike regular Events, the only relevant parameter here for EventCounter is the dictionary containing EventCounterIntervalSec.
                    this.EnableEvents(eventSource, this.level, (EventKeywords)(-1), refreshIntervalDictionary);

                    EventCounterCollectorEventSource.Log.EnabledEventSource(eventSource.Name);
                }
                else
                {
                    EventCounterCollectorEventSource.Log.NotEnabledEventSource(eventSource.Name);
                }
            }
            catch (Exception ex)
            {
                EventCounterCollectorEventSource.Log.EventCounterCollectorError("EventCounterListener EnableEventSource", ex.Message);
            }
        }

        private void ExtractAndPostMetric(string eventSourceName, IDictionary<string, object> eventPayload)
        {
            try
            {
                MetricTelemetry metricTelemetry = new MetricTelemetry();
                bool calculateRate = false;
                double actualValue = 0.0;
                double actualInterval = 0.0;
                int actualCount = 0;
                foreach (KeyValuePair<string, object> payload in eventPayload)
                {
                    var key = payload.Key;
                    if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        var counterName = payload.Value.ToString();
                        if (this.countersToCollect[eventSourceName].Contains(counterName))
                        {
                            metricTelemetry.Name = eventSourceName + "|" + counterName;
                        }
                        else
                        {
                            EventCounterCollectorEventSource.Log.IgnoreEventWrittenAsCounterNotInConfiguredList(eventSourceName, counterName);
                            return;
                        }
                    }
                    else if (key.Equals("Mean", StringComparison.OrdinalIgnoreCase))
                    {
                        actualValue = Convert.ToDouble(payload.Value, CultureInfo.InvariantCulture);                        
                    }
                    else if (key.Equals("Increment", StringComparison.OrdinalIgnoreCase))
                    {
                        // Increment indicates we have to calculate rate.
                        actualValue = Convert.ToDouble(payload.Value, CultureInfo.InvariantCulture);
                        calculateRate = true;
                    }
                    else if (key.Equals("IntervalSec", StringComparison.OrdinalIgnoreCase))
                    {
                        // Even though we configure 60 sec, we parse the actual duration from here. It'll be very close to the configured interval of 60.
                        // If for some reason this value is 0, then we default to 60 sec.
                        actualInterval = Convert.ToDouble(payload.Value, CultureInfo.InvariantCulture);
                        if (actualInterval < this.refreshInternalInSecDouble)
                        {
                            EventCounterCollectorEventSource.Log.EventCounterRefreshIntervalLessThanConfigured(actualInterval, this.refreshInternalInSecDouble);
                        }
                    }
                    else if (key.Equals("Count", StringComparison.OrdinalIgnoreCase))
                    {
                        actualCount = Convert.ToInt32(payload.Value, CultureInfo.InvariantCulture);
                    }
                }

                if (calculateRate)
                {
                    if (actualInterval > 0)
                    {
                        metricTelemetry.Sum = actualValue / actualInterval;
                    }
                    else
                    {
                        metricTelemetry.Sum = actualValue / this.refreshInternalInSecDouble;
                        EventCounterCollectorEventSource.Log.EventCounterIntervalZero(metricTelemetry.Name);
                    }
                }
                else
                {
                    metricTelemetry.Sum = actualValue;
                }

                // This will make the counter appear under PerformanceCounter as opposed to CustomMetrics in Application Insights Analytics(Kusto) tables.
                metricTelemetry.Properties.Add("CustomPerfCounter", "true");
                metricTelemetry.Properties.Add("AggregationInterval", actualInterval.ToString(CultureInfo.InvariantCulture));
                metricTelemetry.Count = actualCount;
                this.telemetryClient.TrackMetric(metricTelemetry);
            }
            catch (Exception ex)
            {
                EventCounterCollectorEventSource.Log.EventCounterCollectorWarning("ExtractMetric", ex.Message);
            }
        }
    }
}