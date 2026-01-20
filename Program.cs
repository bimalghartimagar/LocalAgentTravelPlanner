using LocalAgentTravelPlanner.Agents;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OllamaSharp;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║          🌍 Multi-Agent Travel Planner System 🌍             ║");
        Console.WriteLine("║     Powered by Microsoft Agent Framework + Ollama            ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Configuration
        var ollamaUri = new Uri("http://localhost:11434");
        const string workerModel = "qwen2.5:7b";  // Fast model for worker agents

        Console.WriteLine($"🔧 Connecting to Ollama at {ollamaUri}...");
        Console.WriteLine($"🧠 Using model: {workerModel}");
        Console.WriteLine();

        try
        {
            // Initialize Ollama client with function calling support
            IChatClient baseClient = new OllamaApiClient(ollamaUri, workerModel);
            IChatClient chatClientWithTools = new ChatClientBuilder(baseClient)
                .UseFunctionInvocation()
                .Build();

            // Create specialized agents using factory pattern
            Console.WriteLine("🤖 Initializing agents...");
            
            var researcher = ResearcherAgentFactory.Create(chatClientWithTools);
            Console.WriteLine("   ✅ Researcher Agent (destination data gathering)");
            
            var planner = PlannerAgentFactory.Create(chatClientWithTools);
            Console.WriteLine("   ✅ Planner Agent (itinerary creation)");
            
            var accountant = AccountantAgentFactory.Create(chatClientWithTools);
            Console.WriteLine("   ✅ Accountant Agent (budget analysis)");
            
            // TODO: Phase 2 - Add Auditor Agent with larger model
            // const string auditorModel = "llama3:70b";
            // var auditor = AuditorAgentFactory.Create(auditorClientWithTools);
            // Console.WriteLine("   ✅ Auditor Agent (validation & scoring)");

            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine();

            // Build sequential workflow: Research → Plan → Budget (→ Audit in Phase 2)
            var workflow = AgentWorkflowBuilder.BuildSequential(
                new List<ChatClientAgent> { researcher, planner, accountant }
            );

            // Display example prompts
            Console.WriteLine("📝 Example requests:");
            Console.WriteLine("   • \"3-day family trip from Butwal to Pokhara, budget 50000 NPR\"");
            Console.WriteLine("   • \"5-day solo adventure in Pokhara on a budget of $200\"");
            Console.WriteLine("   • \"Luxury 3-day trip to Pokhara from Kathmandu, budget unlimited\"");
            Console.WriteLine();

            Console.Write("📍 Where do you want to go?\n> ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("❌ No input provided. Exiting.");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine("🔄 Processing your request through the agent pipeline...");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine();

            // Execute workflow with streaming output
            StreamingRun run = await InProcessExecution.StreamAsync(workflow, input);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            await foreach (WorkflowEvent evt in run.WatchStreamAsync().ConfigureAwait(false))
            {
                if (evt is AgentRunUpdateEvent e)
                {
                    // Stream the agent's output
                    Console.Write(e.Data);
                }
                else if (evt is WorkflowOutputEvent)
                {
                    // Workflow complete
                    break;
                }
            }

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine("✅ Travel planning complete!");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"❌ Failed to connect to Ollama: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("💡 Make sure Ollama is running:");
            Console.WriteLine("   1. Open a terminal and run: ollama serve");
            Console.WriteLine($"   2. Pull the model: ollama pull {workerModel}");
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ An error occurred: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("Stack trace:");
            Console.WriteLine(ex.StackTrace);
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }
}
