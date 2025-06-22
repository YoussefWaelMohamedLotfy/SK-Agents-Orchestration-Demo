using Azure.AI.Agents.Persistent;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.OpenAI;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Orchestration.Concurrent;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Orchestration.Handoff;
using Microsoft.SemanticKernel.Agents.Orchestration.Sequential;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.Assistants;
using OpenAI.Chat;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using static BaseOrchestration;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

BaseOrchestration orchestrationConfig = new();
using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddConsole());
string ollamaModelName = "granite3.3";

await RunSequentialOrchestrationExample();
//await RunConcurrentOrchestrationExample();
//await RunGroupChatWithAIManagerOrchestrationExample(); // Issue with StructuredOutput in Ollama
//await RunGroupChatWithHumanInTheLoopOrchestrationExample();
//await RunHandoffOrchestrationExample();
//await RunHandoffWithStructuredInputOrchestrationExample(); // Issue

async Task RunHandoffWithStructuredInputOrchestrationExample()
{
    // Initialize plugin
    GithubPlugin githubPlugin = new();
    KernelPlugin plugin = KernelPluginFactory.CreateFromObject(githubPlugin);

    // Define the agents
    ChatCompletionAgent triageAgent =
        orchestrationConfig.CreateAgent(
            agentId: "triageAgent",
            modelName: ollamaModelName,
            instructions: "Given a GitHub issue, triage it.",
            name: "TriageAgent",
            description: "An agent that triages GitHub issues");

    ChatCompletionAgent pythonAgent =
        orchestrationConfig.CreateAgent(
            agentId: "pythonAgent",
            modelName: ollamaModelName,
            instructions: "You are an agent that handles Python related GitHub issues.",
            name: "PythonAgent",
            description: "An agent that handles Python related issues");
    pythonAgent.Kernel.Plugins.Add(plugin);
   
    ChatCompletionAgent dotnetAgent =
        orchestrationConfig.CreateAgent(
            agentId: "dotnetAgent",
            modelName: ollamaModelName,
            instructions: "You are an agent that handles .NET related GitHub issues.",
            name: "DotNetAgent",
            description: "An agent that handles .NET related issues");
    dotnetAgent.Kernel.Plugins.Add(plugin);

    // Define the orchestration
    HandoffOrchestration<GithubIssue, string> orchestration =
        new(OrchestrationHandoffs
                .StartWith(triageAgent)
                .Add(triageAgent, dotnetAgent, pythonAgent),
            triageAgent,
            pythonAgent,
            dotnetAgent)
        {
            LoggerFactory = factory
        };

    GithubIssue input =
        new()
        {
            Id = "12345",
            Title = "Bug: SQLite Error 1: 'ambiguous column name:' when including VectorStoreRecordKey in VectorSearchOptions.Filter",
            Body =
                """
                    Describe the bug
                    When using column names marked as [VectorStoreRecordData(IsFilterable = true)] in VectorSearchOptions.Filter, the query runs correctly.
                    However, using the column name marked as [VectorStoreRecordKey] in VectorSearchOptions.Filter, the query throws exception 'SQLite Error 1: ambiguous column name: StartUTC'.
                    To Reproduce
                    Add a filter for the column marked [VectorStoreRecordKey]. Since that same column exists in both the vec_TestTable and TestTable, the data for both columns cannot be returned.

                    Expected behavior
                    The query should explicitly list the vec_TestTable column names to retrieve and should omit the [VectorStoreRecordKey] column since it will be included in the primary TestTable columns.

                    Platform
                    Microsoft.SemanticKernel.Connectors.Sqlite v1.46.0-preview

                    Additional context
                    Normal DBContext logging shows only normal context queries. Queries run by VectorizedSearchAsync() don't appear in those logs and I could not find a way to enable logging in semantic search so that I could actually see the exact query that is failing. It would have been very useful to see the failing semantic query.                    
                    """,
            Labels = []
        };

    // Start the runtime
    InProcessRuntime runtime = new();
    await runtime.StartAsync();

    // Run the orchestration
    Console.WriteLine($"\n# INPUT:\n{input.Id}: {input.Title}\n");
    OrchestrationResult<string> result = await orchestration.InvokeAsync(input, runtime);
    string text = await result.GetValueAsync(TimeSpan.FromSeconds(ResultTimeoutInSeconds));
    Console.WriteLine($"\n# RESULT: {text}");
    Console.WriteLine($"\n# LABELS: {string.Join(",", githubPlugin.Labels["12345"])}\n");

    await runtime.RunUntilIdleAsync();
}

async Task RunHandoffOrchestrationExample()
{
    // Define the agents & tools
    ChatCompletionAgent triageAgent =
        orchestrationConfig.CreateAgent(
            agentId: "triageAgent",
            modelName: ollamaModelName,
            instructions: "A customer support agent that triages issues.",
            name: "TriageAgent",
            description: "Handle customer requests.");
    
    ChatCompletionAgent statusAgent =
        orchestrationConfig.CreateAgent(
            agentId: "statusAgent",
            modelName: ollamaModelName,
            name: "OrderStatusAgent",
            instructions: "Handle order status requests.",
            description: "A customer support agent that checks order status.");
    statusAgent.Kernel.Plugins.Add(KernelPluginFactory.CreateFromObject(new OrderStatusPlugin()));
    
    ChatCompletionAgent returnAgent =
        orchestrationConfig.CreateAgent(
            agentId: "returnAgent",
            modelName: ollamaModelName,
            name: "OrderReturnAgent",
            instructions: "Handle order return requests.",
            description: "A customer support agent that handles order returns.");
    returnAgent.Kernel.Plugins.Add(KernelPluginFactory.CreateFromObject(new OrderReturnPlugin()));
    
    ChatCompletionAgent refundAgent =
        orchestrationConfig.CreateAgent(
            agentId: "refundAgent",
            modelName: ollamaModelName,
            name: "OrderRefundAgent",
            instructions: "Handle order refund requests.",
            description: "A customer support agent that handles order refund.");
    refundAgent.Kernel.Plugins.Add(KernelPluginFactory.CreateFromObject(new OrderRefundPlugin()));

    // Create a monitor to capturing agent responses (via ResponseCallback)
    // to display at the end of this sample. (optional)
    // NOTE: Create your own callback to capture responses in your application or service.
    OrchestrationMonitor monitor = new();
    // Define user responses for InteractiveCallback (since sample is not interactive)
    Queue<string> responses = new();
    string task = "I am a customer that needs help with my orders";
    responses.Enqueue("I'd like to track the status of my order");
    responses.Enqueue("My order ID is 123");
    responses.Enqueue("I want to return another order of mine");
    responses.Enqueue("Order ID 321");
    responses.Enqueue("Broken item");
    responses.Enqueue("No, bye");

    // Define the orchestration
    HandoffOrchestration orchestration =
        new(OrchestrationHandoffs
                .StartWith(triageAgent)
                .Add(triageAgent, statusAgent, returnAgent, refundAgent)
                .Add(statusAgent, triageAgent, "Transfer to this agent if the issue is not status related")
                .Add(returnAgent, triageAgent, "Transfer to this agent if the issue is not return related")
                .Add(refundAgent, triageAgent, "Transfer to this agent if the issue is not refund related"),
            triageAgent,
            statusAgent,
            returnAgent,
            refundAgent)
        {
            InteractiveCallback = () =>
            {
                bool valid = responses.TryDequeue(out string? input);
                Console.WriteLine($"\n# INPUT: {input}\n");
                return ValueTask.FromResult(new ChatMessageContent(AuthorRole.User, input ?? "Have a great day"));
            },
            ResponseCallback = monitor.ResponseCallback,
            LoggerFactory = factory
        };

    // Start the runtime
    InProcessRuntime runtime = new();
    await runtime.StartAsync();

    // Run the orchestration
    Console.WriteLine($"\n# INPUT:\n{task}\n");
    OrchestrationResult<string> result = await orchestration.InvokeAsync(task, runtime);

    string text = await result.GetValueAsync(TimeSpan.FromSeconds(300));
    Console.WriteLine($"\n# RESULT: {text}");

    await runtime.RunUntilIdleAsync();

    Console.WriteLine("\n\nORCHESTRATION HISTORY");
    foreach (ChatMessageContent message in monitor.History)
    {
        orchestrationConfig.WriteAgentChatMessage(message);
    }
}

async Task RunGroupChatWithHumanInTheLoopOrchestrationExample()
{
    // Define the agents
    ChatCompletionAgent writer =
        orchestrationConfig.CreateAgent(
            agentId: "copywriter",
            modelName: ollamaModelName,
            instructions:
            """
                You are a copywriter with ten years of experience and are known for brevity and a dry humor.
                The goal is to refine and decide on the single best copy as an expert in the field.
                Only provide a single proposal per response.
                You're laser focused on the goal at hand.
                Don't waste time with chit chat.
                Consider suggestions when refining an idea.
                """,
            description: "A copy writer",
            name: "CopyWriter");
    ChatCompletionAgent editor =
        orchestrationConfig.CreateAgent(
            agentId: "reviewer",
            modelName: ollamaModelName,
            instructions:
            """
                You are an art director who has opinions about copywriting born of a love for David Ogilvy.
                The goal is to determine if the given copy is acceptable to print.
                If so, state that it is approved.
                If not, provide insight on how to refine suggested copy without example.
                """,
            description: "An editor.",
            name: "Reviewer");

    OrchestrationMonitor monitor = new();

    // Define the orchestration
    GroupChatOrchestration orchestration =
        new(
            new CustomRoundRobinGroupChatManager()
            {
                MaximumInvocationCount = 5,
                InteractiveCallback = () =>
                {
                    Console.Write($"\n# Type your message: ");
                    string userInput = Console.ReadLine();
                    ChatMessageContent input = new(AuthorRole.User, userInput);
                    Console.WriteLine($"\n# INPUT: {input.Content}\n");
                    return ValueTask.FromResult(input);
                }
            },
            writer,
            editor)
        {
            ResponseCallback = monitor.ResponseCallback,
            LoggerFactory = factory
        };

    // Start the runtime
    InProcessRuntime runtime = new();
    await runtime.StartAsync();

    // Run the orchestration
    string input = "Create a slogan for a new eletric SUV that is affordable and fun to drive.";
    Console.WriteLine($"\n# INPUT: {input}\n");
    OrchestrationResult<string> result = await orchestration.InvokeAsync(input, runtime);
    string text = await result.GetValueAsync(TimeSpan.FromSeconds(ResultTimeoutInSeconds * 3));
    Console.WriteLine($"\n# RESULT: {text}");

    await runtime.RunUntilIdleAsync();

    Console.WriteLine("\n\nORCHESTRATION HISTORY");
    foreach (ChatMessageContent message in monitor.History)
    {
        orchestrationConfig.WriteAgentChatMessage(message);
    }
}

async Task RunGroupChatWithAIManagerOrchestrationExample()
{
    // Define the agents
    ChatCompletionAgent farmer =
        orchestrationConfig.CreateAgent(
            agentId: "farmer",
            modelName: ollamaModelName,
            instructions:
            """
                You're a farmer from Southeast Asia. 
                Your life is deeply connected to land and family. 
                You value tradition and sustainability. 
                You are in a debate. Feel free to challenge the other participants with respect.
                """,
            description: "A rural farmer from Southeast Asia.",
            name: "Farmer");
    ChatCompletionAgent developer =
        orchestrationConfig.CreateAgent(
            agentId: "developer",
            modelName: ollamaModelName,
            instructions:
            """
                You're a software developer from the United States. 
                Your life is fast-paced and technology-driven. 
                You value innovation, freedom, and work-life balance. 
                You are in a debate. Feel free to challenge the other participants with respect.
                """,
            description: "An urban software developer from the United States.",
            name: "Developer");
    ChatCompletionAgent teacher =
        orchestrationConfig.CreateAgent(
            agentId: "teacher",
            modelName: ollamaModelName,
            instructions:
            """
                You're a retired history teacher from Eastern Europe. 
                You bring historical and philosophical perspectives to discussions. 
                You value legacy, learning, and cultural continuity. 
                You are in a debate. Feel free to challenge the other participants with respect.
                """,
            description: "A retired history teacher from Eastern Europe",
            name: "Teacher");
    ChatCompletionAgent activist =
        orchestrationConfig.CreateAgent(
            agentId: "activist",
            modelName: ollamaModelName,
            instructions:
            """
                You're a young activist from South America. 
                You focus on social justice, environmental rights, and generational change. 
                You are in a debate. Feel free to challenge the other participants with respect.
                """,
            description: "A young activist from South America.",
            name: "Activist");
    ChatCompletionAgent spiritual =
        orchestrationConfig.CreateAgent(
            agentId: "spiritual",
            modelName: ollamaModelName,
            instructions:
            """
                You're a spiritual leader from the Middle East. 
                You provide insights grounded in religion, morality, and community service. 
                You are in a debate. Feel free to challenge the other participants with respect.
                """,
            description: "A spiritual leader from the Middle East.",
            name: "SpiritualLeader");
    ChatCompletionAgent artist =
        orchestrationConfig.CreateAgent(
            agentId: "artist",
            modelName: ollamaModelName,
            instructions:
            """
                You're an artist from Africa. 
                You view life through creative expression, storytelling, and collective memory. 
                You are in a debate. Feel free to challenge the other participants with respect.
                """,
            description: "An artist from Africa.",
            name: "Artist");
    ChatCompletionAgent immigrant =
        orchestrationConfig.CreateAgent(
            agentId: "immigrant",
            modelName: ollamaModelName,
            instructions:
            """
                You're an immigrant entrepreneur from Asia living in Canada. 
                You balance trandition with adaption. 
                You focus on family success, risk, and opportunity. 
                You are in a debate. Feel free to challenge the other participants with respect.
                """,
            description: "An immigrant entrepreneur from Asia living in Canada.",
            name: "Immigrant");
    ChatCompletionAgent doctor =
        orchestrationConfig.CreateAgent(
            agentId: "doctor",
            modelName: ollamaModelName,
            instructions:
            """
                You're a doctor from Scandinavia. 
                Your perspective is shaped by public health, equity, and structured societal support. 
                You are in a debate. Feel free to challenge the other participants with respect.
                """,
            description: "A doctor from Scandinavia.",
            name: "Doctor");

    // Define the orchestration
    const string topic = "What does a good life mean to you personally?";
    Kernel kernel = orchestrationConfig.CreateKernelWithChatCompletion(ollamaModelName);
    GroupChatOrchestration orchestration =
        new(
            new AIGroupChatManager(
                topic,
                kernel.GetRequiredService<IChatCompletionService>())
            {
                MaximumInvocationCount = 5,
            },
            farmer,
            developer,
            teacher,
            activist,
            spiritual,
            artist,
            immigrant,
            doctor)
        {
            LoggerFactory = factory
        };

    // Start the runtime
    InProcessRuntime runtime = new();
    await runtime.StartAsync();

    // Run the orchestration
    Console.WriteLine($"\n# INPUT: {topic}\n");
    OrchestrationResult<string> result = await orchestration.InvokeAsync(topic, runtime);
    string text = await result.GetValueAsync(TimeSpan.FromSeconds(ResultTimeoutInSeconds * 3));
    Console.WriteLine($"\n# RESULT: {text}");

    await runtime.RunUntilIdleAsync();
}

async Task RunConcurrentOrchestrationExample()
{
    // Define the agents
    ChatCompletionAgent pythonDeveloper =
        orchestrationConfig.CreateAgent(
            agentId: "pythonDeveloper",
            modelName: ollamaModelName,
            instructions: """
            
            You are an expert software developer in Python. You answer questions from a Python development perspective.
            All code snippets you output must be in Python.
            """,
            description: "An expert in Python Programming");
    ChatCompletionAgent csharpDeveloper =
        orchestrationConfig.CreateAgent(
            agentId: "csharpDeveloper",
            modelName: ollamaModelName,
            instructions: """
            You are an expert software developer in CSharp. You answer questions from a CSharp development perspective.
            All code snippets you output must be in CSharp.
            """,
            description: "An expert in Csharp programming");

    // Create a monitor to capturing agent responses (via ResponseCallback)
    // to display at the end of this sample. (optional)
    // NOTE: Create your own callback to capture responses in your application or service.
    OrchestrationMonitor monitor = new();

    // Define the orchestration
    ConcurrentOrchestration orchestration =
        new(csharpDeveloper, pythonDeveloper)
        {
            ResponseCallback = monitor.ResponseCallback,
            LoggerFactory = factory,
        };

    // Start the runtime
    InProcessRuntime runtime = new();
    await runtime.StartAsync();

    // Run the orchestration
    string input = "Write the Binary Search Algorithm";
    Console.WriteLine($"\n# INPUT: {input}\n");
    OrchestrationResult<string[]> result = await orchestration.InvokeAsync(input, runtime);

    string[] output = await result.GetValueAsync(TimeSpan.FromSeconds(ResultTimeoutInSeconds));
    Console.WriteLine($"\n# RESULT:\n{string.Join("\n\n", output.Select(text => $"{text}"))}");

    await runtime.RunUntilIdleAsync();

    Console.WriteLine("\n\nORCHESTRATION HISTORY");
    foreach (ChatMessageContent message in monitor.History)
    {
        orchestrationConfig.WriteAgentChatMessage(message);
    }
}

async Task RunSequentialOrchestrationExample()
{
    Console.WriteLine("Running Sequential Orchestration Example...");

    // Define the agents
    ChatCompletionAgent analystAgent =
        orchestrationConfig.CreateAgent(
            agentId: "analyst",
            modelName: ollamaModelName,
            name: "Analyst",
            instructions:
            """
                You are a marketing analyst. Given a product description, identify:
                - Key features
                - Target audience
                - Unique selling points
                """,
            description: "A agent that extracts key concepts from a product description.");

    ChatCompletionAgent writerAgent =
        orchestrationConfig.CreateAgent(
            agentId: "writer",
            modelName: ollamaModelName,
            name: "copywriter",
            instructions:
            """
                You are a marketing copywriter. Given a block of text describing features, audience, and USPs,
                compose a compelling marketing copy (like a newsletter section) that highlights these points.
                Output should be short (around 150 words), output just the copy as a single text block.
                """,
            description: "An agent that writes a marketing copy based on the extracted concepts.");
    ChatCompletionAgent editorAgent =
        orchestrationConfig.CreateAgent(
            agentId: "editor",
            modelName: ollamaModelName,
            name: "editor",
            instructions:
            """
                You are an editor. Given the draft copy, correct grammar, improve clarity, ensure consistent tone,
                give format and make it polished. Output the final improved copy as a single text block.
                """,
            description: "An agent that formats and proofreads the marketing copy.");

    // Create a monitor to capturing agent responses (via ResponseCallback)
    // to display at the end of this sample. (optional)
    // NOTE: Create your own callback to capture responses in your application or service.
    OrchestrationMonitor monitor = new();
    // Define the orchestration
    SequentialOrchestration orchestration =
        new(analystAgent, writerAgent, editorAgent)
        {
            ResponseCallback = monitor.ResponseCallback,
            LoggerFactory = factory
        };

    // Start the runtime
    InProcessRuntime runtime = new();
    await runtime.StartAsync();

    // Run the orchestration
    string input = "An eco-friendly stainless steel water bottle that keeps drinks cold for 24 hours";
    Console.WriteLine($"\n# INPUT: {input}\n");

    OrchestrationResult<string> result = await orchestration.InvokeAsync(input, runtime);
    string text = await result.GetValueAsync(TimeSpan.FromSeconds(ResultTimeoutInSeconds));
    Console.WriteLine($"\n# RESULT: {text}");

    await runtime.RunUntilIdleAsync();

    Console.WriteLine("\n\nORCHESTRATION HISTORY");

    foreach (Microsoft.SemanticKernel.ChatMessageContent message in monitor.History)
    {
        orchestrationConfig.WriteAgentChatMessage(message);
    }
}

public sealed class BaseOrchestration //: BaseAgentsTest
{
    public const int ResultTimeoutInSeconds = 360;

    public ChatCompletionAgent CreateAgent(string agentId, string modelName, string instructions, string? description = null, string? name = null, Kernel? kernel = null)
    {
        return
            new ChatCompletionAgent
            {
                Id = agentId,
                Name = name,
                Description = description,
                Instructions = instructions,
                Kernel = kernel ?? CreateKernelWithChatCompletion(modelName),
                Arguments =
                new KernelArguments(
                    new OllamaPromptExecutionSettings
                    {
                        FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
                        Temperature = 0,
                    }),
            };
    }

    public ChatCompletionAgent CreateAgent(string name, string instructions, string description, Kernel kernel) =>
        new()
        {
            Name = name,
            Instructions = instructions,
            Description = description,
            Kernel = kernel.Clone(),
            Arguments =
                new KernelArguments(
                    new OllamaPromptExecutionSettings
                    {
                        FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
                        Temperature = 0,
                    }),
        };

    public Kernel CreateKernelWithChatCompletion(string modelName) =>
        Kernel.CreateBuilder()
            .AddOllamaChatCompletion(
                modelId: modelName,
                endpoint: new Uri("http://localhost:11434"),
                serviceId: modelName)
            .Build();

    /// <summary>
    /// Common method to write formatted agent chat content to the console.
    /// </summary>
    public void WriteAgentChatMessage(Microsoft.SemanticKernel.ChatMessageContent message)
    {
        // Include ChatMessageContent.AuthorName in output, if present.
        string authorExpression = message.Role == AuthorRole.User ? string.Empty : $" - {message.AuthorName ?? "*"}";
        // Include TextContent (via ChatMessageContent.Content), if present.
        string contentExpression = string.IsNullOrWhiteSpace(message.Content) ? string.Empty : message.Content;
        bool isCode = message.Metadata?.ContainsKey(OpenAIAssistantAgent.CodeInterpreterMetadataKey) ?? false;
        string codeMarker = isCode ? "\n  [CODE]\n" : " ";
        Console.WriteLine($"\n# {message.Role}{authorExpression}:{codeMarker}{contentExpression}");

        // Provide visibility for inner content (that isn't TextContent).
        foreach (KernelContent item in message.Items)
        {
            if (item is AnnotationContent annotation)
            {
                if (annotation.Kind == AnnotationKind.UrlCitation)
                {
                    Console.WriteLine($"  [{item.GetType().Name}] {annotation.Label}: {annotation.ReferenceId} - {annotation.Title}");
                }
                else
                {
                    Console.WriteLine($"  [{item.GetType().Name}] {annotation.Label}: File #{annotation.ReferenceId}");
                }
            }
            else if (item is FileReferenceContent fileReference)
            {
                Console.WriteLine($"  [{item.GetType().Name}] File #{fileReference.FileId}");
            }
            else if (item is ImageContent image)
            {
                Console.WriteLine($"  [{item.GetType().Name}] {image.Uri?.ToString() ?? image.DataUri ?? $"{image.Data?.Length} bytes"}");
            }
            else if (item is FunctionCallContent functionCall)
            {
                Console.WriteLine($"  [{item.GetType().Name}] {functionCall.Id}");
            }
            else if (item is FunctionResultContent functionResult)
            {
                Console.WriteLine($"  [{item.GetType().Name}] {functionResult.CallId} - {functionResult.Result ?? "*"}");
            }
        }

        if (message.Metadata?.TryGetValue("Usage", out object? usage) ?? false)
        {
            if (usage is RunStepTokenUsage assistantUsage)
            {
                WriteUsage(assistantUsage.TotalTokenCount, assistantUsage.InputTokenCount, assistantUsage.OutputTokenCount);
            }
            else if (usage is RunStepCompletionUsage agentUsage)
            {
                WriteUsage(agentUsage.TotalTokens, agentUsage.PromptTokens, agentUsage.CompletionTokens);
            }
            else if (usage is ChatTokenUsage chatUsage)
            {
                WriteUsage(chatUsage.TotalTokenCount, chatUsage.InputTokenCount, chatUsage.OutputTokenCount);
            }
        }

        void WriteUsage(long totalTokens, long inputTokens, long outputTokens)
        {
            Console.WriteLine($"  [Usage] Tokens: {totalTokens}, Input: {inputTokens}, Output: {outputTokens}");
        }
    }

    public sealed class OrchestrationMonitor
    {
        public ChatHistory History { get; } = [];

        public ValueTask ResponseCallback(Microsoft.SemanticKernel.ChatMessageContent response)
        {
            History.Add(response);
            Console.WriteLine($"\n~ Agent {response.AuthorName} response: {response}\n");
            return ValueTask.CompletedTask;
        }
    }
}

sealed class AIGroupChatManager(string topic, IChatCompletionService chatCompletion) : GroupChatManager
{
    private static class Prompts
    {
        public static string Termination(string topic) =>
            $"""
                You are mediator that guides a discussion on the topic of '{topic}'. 
                You need to determine if the discussion has reached a conclusion. 
                If you would like to end the discussion, please respond with True. Otherwise, respond with False.
                Response should be in JSON object format with the value of "value" set to true or false, and the
                value of "reason" set to any text output.
                No other text or sentences must be output including the word "assistant". Only the JSON response.
                """;

        public static string Selection(string topic, string participants) =>
            $"""
                You are mediator that guides a discussion on the topic of '{topic}'. 
                You need to select the next participant to speak. 
                Here are the names and descriptions of the participants: 
                {participants}\n
                Please respond with only the name of the participant you would like to select.
                Response should be in JSON object format with the value of "value" set to the name of the participant.
                No other text or sentences must be output including the word "assistant". Only the JSON response.
                """;

        public static string Filter(string topic) =>
            $"""
                You are mediator that guides a discussion on the topic of '{topic}'. 
                You have just concluded the discussion. 
                Please summarize the discussion and provide a closing statement.
                Response should be in JSON object format with the value of "value" set to the summary of the discussion.
                No other text or sentences must be output including the word "assistant". Only the JSON response.
                """;
    }

    readonly JsonSerializerOptions options = JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public override ValueTask<GroupChatManagerResult<string>> FilterResults(ChatHistory history, CancellationToken cancellationToken = default) =>
        this.GetResponseAsync<string>(history, Prompts.Filter(topic), cancellationToken);

    /// <inheritdoc/>
    public override ValueTask<GroupChatManagerResult<string>> SelectNextAgent(ChatHistory history, GroupChatTeam team, CancellationToken cancellationToken = default) =>
        this.GetResponseAsync<string>(history, Prompts.Selection(topic, team.FormatList()), cancellationToken);

    /// <inheritdoc/>
    public override ValueTask<GroupChatManagerResult<bool>> ShouldRequestUserInput(ChatHistory history, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new GroupChatManagerResult<bool>(false) { Reason = "The AI group chat manager does not request user input." });

    /// <inheritdoc/>
    public override async ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(ChatHistory history, CancellationToken cancellationToken = default)
    {
        GroupChatManagerResult<bool> result = await base.ShouldTerminate(history, cancellationToken);
        if (!result.Value)
        {
            result = await this.GetResponseAsync<bool>(history, Prompts.Termination(topic), cancellationToken);
        }
        return result;
    }

    private async ValueTask<GroupChatManagerResult<TValue>> GetResponseAsync<TValue>(ChatHistory history, string prompt, CancellationToken cancellationToken = default)
    {
        JsonNode schema = options.GetJsonSchemaAsNode(typeof(GroupChatManagerResult<TValue>));
        var x = schema.ToString();

        Dictionary<string, object> extensionData = new()
        {
            { "format", schema.ToString() },
        };
        OllamaPromptExecutionSettings executionSettings = new() { ExtensionData = extensionData };
        ChatHistory request = [.. history, new ChatMessageContent(AuthorRole.System, prompt)];
        ChatMessageContent response = await chatCompletion.GetChatMessageContentAsync(request, executionSettings, kernel: null, cancellationToken);
        string responseText = response.ToString();
        return
            JsonSerializer.Deserialize<GroupChatManagerResult<TValue>>(responseText) ??
            throw new InvalidOperationException($"Failed to parse response: {responseText}");
    }
}

/// <summary>
/// Define a custom group chat manager that enables user input.
/// </summary>
/// <remarks>
/// User input is achieved by overriding the default round robin manager
/// to allow user input after the reviewer agent's message.
/// </remarks>
sealed class CustomRoundRobinGroupChatManager : RoundRobinGroupChatManager
{
    public override ValueTask<GroupChatManagerResult<bool>> ShouldRequestUserInput(ChatHistory history, CancellationToken cancellationToken = default)
    {
        string? lastAgent = history.LastOrDefault()?.AuthorName;

        if (lastAgent is null)
        {
            return ValueTask.FromResult(new GroupChatManagerResult<bool>(false) { Reason = "No agents have spoken yet." });
        }

        if (lastAgent == "Reviewer")
        {
            return ValueTask.FromResult(new GroupChatManagerResult<bool>(true) { Reason = "User input is needed after the reviewer's message." });
        }

        return ValueTask.FromResult(new GroupChatManagerResult<bool>(false) { Reason = "User input is not needed until the reviewer's message." });
    }
}

sealed class OrderStatusPlugin
{
    [KernelFunction]
    public string CheckOrderStatus(string orderId) => $"Order {orderId} is shipped and will arrive in 2-3 days.";
}

sealed class OrderReturnPlugin
{
    [KernelFunction]
    public string ProcessReturn(string orderId, string reason) => $"Return for order {orderId} has been processed successfully.";
}

sealed class OrderRefundPlugin
{
    [KernelFunction]
    public string ProcessReturn(string orderId, string reason) => $"Refund for order {orderId} has been processed successfully.";
}

sealed class GithubIssue
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("labels")]
    public string[] Labels { get; set; } = [];
}

sealed class GithubPlugin
{
    public Dictionary<string, string[]> Labels { get; } = [];

    [KernelFunction]
    public void AddLabels(string issueId, params string[] labels)
    {
        this.Labels[issueId] = labels;
    }
}