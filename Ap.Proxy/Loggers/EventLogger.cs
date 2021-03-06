﻿using System;
using System.Threading.Tasks;
using Ap.Logs;

namespace Ap.Proxy.Loggers
{
    public class EventLogger : ILogger
    {
        public Task WriteAsync(string summary, string description, EventType type, string connectionId)
        {
            return Task.Run(() => LogStore.Push(summary, description, DateTime.UtcNow, type, connectionId));
        }

        public void Flush() { }

        public bool IsEnabledFor(string method) { return true; }
    }
}
