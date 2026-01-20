using Microsoft.Agents.AI;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace LocalAgentTravelPlanner
{
    internal class CustomAgentThread : InMemoryAgentThread
    {
        internal CustomAgentThread() : base() { }
        internal CustomAgentThread(JsonElement serializedThreadState, JsonSerializerOptions? jsonSerializerOptions = null)
            : base(serializedThreadState, jsonSerializerOptions) { }
    }
}
