﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Threading;

namespace Microsoft.Azure.WebJobs.Host.Scale
{
    /// <summary>
    /// Represents the concurrency status for a function.
    /// </summary>
    public class ConcurrencyStatus
    {
        internal const int NextStatusDelayDefaultSeconds = 1;
        internal const int DefaultFailedAdjustmentQuietWindowSeconds = 30;
        internal const int AdjustmentRunWindowSeconds = 10;
        internal const int DefaultMinAdjustmentFrequencySeconds = 5;
        internal const int MaxAdjustmentDelta = 5;

        private readonly ConcurrencyManager _concurrencyManager;
        private readonly object _syncLock = new object();

        private int _adjustmentRunDirection;
        private int _adjustmentRunCount;

        /// <summary>
        /// Constructs a new instance.
        /// </summary>
        /// <param name="functionId">The function ID this status is for.</param>
        /// <param name="concurrencyManager">The <see cref="ConcurrencyManager"/>.</param>
        public ConcurrencyStatus(string functionId, ConcurrencyManager concurrencyManager)
        {
            _concurrencyManager = concurrencyManager ?? throw new ArgumentNullException(nameof(concurrencyManager));

            if (string.IsNullOrEmpty(functionId))
            {
                throw new ArgumentNullException(nameof(functionId));
            }
            FunctionId = functionId;
            
            CurrentConcurrency = 1;
            OutstandingInvocations = 0;
            MaxConcurrentExecutionsSinceLastAdjustment = 0;
            _adjustmentRunDirection = 0;

            // We don't start the decrease stopwatch initially because we only want
            // to take it into consideration when a decrease has actually happened.
            // We start the adjustment stopwatch immediately, because we want don't
            // want the first adjustment to happen immediately.
            LastConcurrencyAdjustmentStopwatch = Stopwatch.StartNew();
            LastConcurrencyDecreaseStopwatch = new Stopwatch();
        }

        /// <summary>
        /// Gets the function ID this status is for.
        /// </summary>
        public string FunctionId { get; }

        /// <summary>
        /// Gets the current number of new invocations of this function the host can process.
        /// When throttling is enabled, this may return 0 meaning no new invocations should be
        /// started.
        /// </summary>
        public int AvailableInvocationCount
        {
            get
            {
                if (_concurrencyManager.ThrottleEnabled || OutstandingInvocations > CurrentConcurrency)
                {
                    // we can't take any work right now
                    return 0;
                }
                else
                {
                    // no throttles are enabled, so we can take work up to the current concurrency level
                    return CurrentConcurrency - OutstandingInvocations;
                }
            }
        }

        /// <summary>
        /// Gets the current concurrency level for this function. This adjusts
        /// dynamically over time.
        /// </summary>
        public int CurrentConcurrency { get; internal set; }

        /// <summary>
        /// Gets the current number of in progress invocations of this function.
        /// </summary>
        public int OutstandingInvocations { get; internal set; }

        /// <summary>
        /// Gets the recommended delay for the next time <see cref="ConcurrencyManager.GetStatus(string)"/> should be called
        /// for this function.
        /// </summary>
        public TimeSpan NextStatusDelay
        {
            get
            {
                if (AvailableInvocationCount == 0)
                {
                    // currently hardcoded, but can be made dynamic/configurable in the future
                    // if the host is currently throttling or otherwise can't take any more work,
                    // caller should delay the next status call, to give time for the situation
                    // to change
                    return TimeSpan.FromSeconds(NextStatusDelayDefaultSeconds);
                }
                else
                {
                    return TimeSpan.Zero;
                }
            }
        }

        /// <summary>
        /// Gets a Stopwatch measuring the time since concurrency was last adjusted either up
        /// or down for this function.
        /// </summary>
        internal Stopwatch LastConcurrencyAdjustmentStopwatch { get; }

        /// <summary>
        /// Gets a Stopwatch measuring the time since concurrency was last adjusted down
        /// for this function.
        /// </summary>
        internal Stopwatch LastConcurrencyDecreaseStopwatch { get; }

        /// <summary>
        /// Gets or sets the highest actual invocation concurrency observed since the last
        /// concurrency adjustment.
        /// </summary>
        internal int MaxConcurrentExecutionsSinceLastAdjustment { get; set; }

        /// <summary>
        /// Gets or sets the number of function invocations since the last time
        /// concurrency was adjusted for this function.
        /// </summary>
        internal int InvocationsSinceLastAdjustment { get; set; }

        /// <summary>
        /// Gets or sets the total amount of time the function has run since the last time
        /// concurrency was adjusted.
        /// </summary>
        internal double TotalInvocationTimeSinceLastAdjustmentMs { get; set; }

        internal void ApplySnapshot(FunctionConcurrencySnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            lock (_syncLock)
            {
                if (snapshot.Concurrency > CurrentConcurrency)
                {
                    CurrentConcurrency = snapshot.Concurrency;
                }
            }
        }

        internal bool CanAdjustConcurrency()
        {
            // Don't adjust too often, either up or down if we've made an adjustment recently.
            TimeSpan timeSinceLastAdjustment = LastConcurrencyAdjustmentStopwatch.Elapsed;
            TimeSpan minAdjustmentFrequency = GetLatencyAdjustedInterval(TimeSpan.FromSeconds(ProcessMonitor.DefaultSampleIntervalSeconds * 2), TimeSpan.FromSeconds(DefaultMinAdjustmentFrequencySeconds), 1);

            return timeSinceLastAdjustment > minAdjustmentFrequency;
        }

        internal bool CanDecreaseConcurrency()
        {
            return CurrentConcurrency > 1;
        }

        internal bool CanIncreaseConcurrency(int maxDegreeOfParallelism)
        {
            var timeSinceLastDecrease = LastConcurrencyDecreaseStopwatch.IsRunning ? LastConcurrencyDecreaseStopwatch.Elapsed : TimeSpan.MaxValue;
            TimeSpan minDecreaseQuietWindow = GetLatencyAdjustedInterval(TimeSpan.FromSeconds(ProcessMonitor.DefaultSampleIntervalSeconds * 10), TimeSpan.FromSeconds(DefaultFailedAdjustmentQuietWindowSeconds), 10);
            if (timeSinceLastDecrease < minDecreaseQuietWindow)
            {
                // if we've had a recent failed adjustment, we'll avoid any increases for a while
                return false;
            }

            if (MaxConcurrentExecutionsSinceLastAdjustment < CurrentConcurrency)
            {
                // We only want to increase if we're fully utilizing our current concurrency level.
                // E.g. if we increased to a high concurrency level, then events slowed to a trickle,
                // we wouldn't want to keep increasing.
                return false;
            }

            // a max parallelism of -1 indicates unbounded
            return maxDegreeOfParallelism == -1 || CurrentConcurrency < maxDegreeOfParallelism;
        }

        internal TimeSpan GetLatencyAdjustedInterval(TimeSpan minInterval, TimeSpan defaultInterval, int latencyMultiplier)
        {
            TimeSpan resultInterval = defaultInterval;

            if (InvocationsSinceLastAdjustment > 0)
            {
                // Compute based on function latency, so faster functions can adjust more often, allowing their concurrency
                // to "break away" from longer running, heavier functions.
                // While a longer running function might not actually be a problem (e.g. might be I/O bound),
                // this latency based prioritization is still beneficial. It allows fast functions to give
                // us fast feedback. Worst case, for long running functions we just revert back to the default
                // interval. So this is an optimization applied when possible.
                int avgInvocationLatencyMs = GetAverageInvocationLatencyMS();
                int computedIntervalMS = (int)minInterval.TotalMilliseconds + latencyMultiplier * avgInvocationLatencyMs;
                resultInterval = TimeSpan.FromMilliseconds(Math.Min(computedIntervalMS, (int)defaultInterval.TotalMilliseconds));
            }

            return resultInterval;
        }

        internal void IncreaseConcurrency()
        {
            int delta = GetNextAdjustment(1);
            AdjustConcurrency(delta);
        }

        internal void DecreaseConcurrency()
        {
            int delta = GetNextAdjustment(-1);
            AdjustConcurrency(delta);
        }

        internal void FunctionStarted()
        {
            lock (_syncLock)
            {
                OutstandingInvocations++;

                if (OutstandingInvocations > MaxConcurrentExecutionsSinceLastAdjustment)
                {
                    // record the high water mark for utilized concurrency this interval
                    MaxConcurrentExecutionsSinceLastAdjustment = OutstandingInvocations;
                }
            }
        }

        internal void FunctionCompleted(TimeSpan latency)
        {
            lock (_syncLock)
            {
                TotalInvocationTimeSinceLastAdjustmentMs += latency.TotalMilliseconds;
                InvocationsSinceLastAdjustment++;
                if (OutstandingInvocations > 0)
                {
                    OutstandingInvocations--;
                }
            }
        }

        internal int GetNextAdjustment(int direction)
        {
            // keep track of consecutive adjustment runs in the same direction
            // so we can increase velocity
            TimeSpan timeSinceLastAdjustment = LastConcurrencyAdjustmentStopwatch.Elapsed;
            int adjustmentRunCount = _adjustmentRunCount;
            if ((_adjustmentRunDirection != 0 && _adjustmentRunDirection != direction) || timeSinceLastAdjustment.TotalSeconds > AdjustmentRunWindowSeconds)
            {
                // clear our adjustment run if we change direction or too
                // much time has elapsed since last change
                // when we change directions, our last move might have been large,
                // but well move back in the other direction slowly
                adjustmentRunCount = _adjustmentRunCount = 0;
            }
            else
            {
                // increment for next cycle
                _adjustmentRunCount++;
            }
            _adjustmentRunDirection = direction;

            // based on consecutive moves in the same direction, we'll adjust velocity
            int speedFactor = Math.Min(MaxAdjustmentDelta, adjustmentRunCount);
            return direction * (1 + speedFactor);
        }

        private void AdjustConcurrency(int delta)
        {
            if (delta < 0)
            {
                // if we're adjusting down, restart the stopwatch to delay any further
                // increase attempts for a period to allow things to stabilize at the new
                // concurrency level
                LastConcurrencyDecreaseStopwatch.Restart();
            }

            LastConcurrencyAdjustmentStopwatch.Restart();

            // ensure we don't adjust below 1
            int newConcurrency = CurrentConcurrency + delta;
            newConcurrency = Math.Max(1, newConcurrency);

            lock (_syncLock)
            {
                CurrentConcurrency = newConcurrency;
                MaxConcurrentExecutionsSinceLastAdjustment = 0;
                InvocationsSinceLastAdjustment = 0;
                TotalInvocationTimeSinceLastAdjustmentMs = 0;
            }
        }

        private int GetAverageInvocationLatencyMS()
        {
            int avgInvocationLatencyMs;
            lock (_syncLock)
            {
                avgInvocationLatencyMs = (int)TotalInvocationTimeSinceLastAdjustmentMs / InvocationsSinceLastAdjustment;
            }
            return avgInvocationLatencyMs;
        }
    }
}
