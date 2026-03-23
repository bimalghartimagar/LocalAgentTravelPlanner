using LocalAgentTravelPlanner.Agents;
using LocalAgentTravelPlanner.Services;
using LocalAgentTravelPlanner.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║          Multi-Agent Travel Planner System                   ║");
        Console.WriteLine("║     Powered by Microsoft Agent Framework                     ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Parse command line for provider preference
        // Usage: dotnet run -- --provider anthropic
        //    or: dotnet run -- --provider ollama
        string? preferredProvider = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--provider")
            {
                preferredProvider = args[i + 1];
                break;
            }
        }

        try
        {
            // Create chat client using factory (auto-detects or uses preference)
            var (chatClientWithTools, provider, model) = ChatClientFactory.CreateWithAutoDetect(preferredProvider);

            Console.WriteLine($"Provider: {provider}");
            Console.WriteLine($"Model: {model}");
            Console.WriteLine();

            // Create shared tools with a single HttpClient instance
            var sharedHttp = new HttpClient();
            var researchTools = new ResearchTools(sharedHttp);
            var travelTools = new TravelTools(sharedHttp);

            // Create specialized agents using factory pattern
            Console.WriteLine("🤖 Initializing agents...");

            var researcher = ResearcherAgentFactory.Create(chatClientWithTools, researchTools, travelTools);
            Console.WriteLine("   ✅ Researcher Agent (destination data gathering)");
            
            var planner = PlannerAgentFactory.Create(chatClientWithTools);
            Console.WriteLine("   ✅ Planner Agent (itinerary creation)");
            
            var accountant = AccountantAgentFactory.Create(chatClientWithTools);
            Console.WriteLine("   ✅ Accountant Agent (budget analysis)");

            // Auditor Agent - The validation checkpoint
            // NOTE: In production, you might use a larger model for the Auditor
            // because it needs to carefully analyze and judge the entire plan.
            // For now, we use the same model to keep things simple.
            var auditor = AuditorAgentFactory.Create(chatClientWithTools);
            Console.WriteLine("   ✅ Auditor Agent (validation & scoring)");

            // Aggregator Agent - Creates the final user-friendly output
            // Takes all previous outputs and synthesizes them into a polished document
            var aggregator = AggregatorAgentFactory.Create(chatClientWithTools);
            Console.WriteLine("   ✅ Aggregator Agent (final presentation)");

            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine();

            // Build sequential workflow: Research → Plan → Budget → Audit → Aggregate
            // Each agent receives the full conversation history from previous agents.
            // This enables the Auditor to verify claims and the Aggregator to synthesize.
            var workflow = AgentWorkflowBuilder.BuildSequential(
                new List<ChatClientAgent> { researcher, planner, accountant, auditor, aggregator }
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
                    Console.Write(e.Data?.ToString());
                }
                else if (evt is ExecutorFailedEvent failedEvt)
                {
                    var ex = failedEvt.Data as Exception;
                    var innerMsg = ex?.InnerException?.Message ?? ex?.Message ?? "Unknown error";
                    Console.WriteLine($"\n❌ Agent failed: {innerMsg}");
                }
                else if (evt is WorkflowErrorEvent errorEvt)
                {
                    var ex = errorEvt.Data as Exception;
                    var innerMsg = ex?.InnerException?.Message ?? ex?.Message ?? "Unknown error";
                    Console.WriteLine($"❌ Workflow error: {innerMsg}");
                    break;
                }
                else if (evt is WorkflowOutputEvent)
                {
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
            Console.WriteLine($"Connection error: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("If using Ollama, make sure it's running:");
            Console.WriteLine("   1. Open a terminal and run: ollama serve");
            Console.WriteLine("   2. Pull a model: ollama pull qwen2.5:7b");
            Console.WriteLine();
            Console.WriteLine("If using Anthropic, check your API key:");
            Console.WriteLine("   set ANTHROPIC_API_KEY=your-api-key");
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
