using Microsoft.Extensions.FileProviders;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;

#region Setting up HTTP Client
var builder = new ConfigurationBuilder().AddUserSecrets<Program>();
var config = builder.Build();
var openAIApiKey = config["OpenAI_API_Key"];

var httpClient = new HttpClient()
{
    BaseAddress = new Uri("https://api.openai.com/")
};
httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", openAIApiKey);
httpClient.DefaultRequestHeaders.Add("OpenAI-Beta", "assistants=v1");
httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
#endregion

#region Get a list of all assistants
var assistants = await httpClient.GetFromJsonAsync<OaiResult<Assistant>>("v1/assistants");
var existingAssistant = assistants!.Data.FirstOrDefault(a => a.Name == "Cody's Assistant");
#endregion

#region Define data for assistant
var assistant = new Assistant(
    "Cody's Assistant",
    "Assistant for suggesting what to do in Vienna",
    "gpt-3.5-turbo-1106",
    """
    You are a helpful assistant suggesting what to do in Vienna.
    Answer always in English. End last sentence with ", voila!". 
    """,
    []
);
#endregion

#region Update or create the assistant
var response = new HttpResponseMessage();
if (existingAssistant is null)
{
    Console.WriteLine("Creating assistant...");
    response = await httpClient.PostAsJsonAsync("v1/assistants", assistant);
}
else
{
    if (assistant.Name != existingAssistant.Name
        || assistant.Description != existingAssistant.Description
        || assistant.Instructions != existingAssistant.Instructions
        || assistant.Model != existingAssistant.Model)
    {
        Console.WriteLine("Updating assistant...");
        response = await httpClient.PostAsJsonAsync($"v1/assistants/{existingAssistant.Id}", assistant);
    }
    else { Console.WriteLine("Assistant is up to date."); }
}
/*
if (response is not null)
{
    response?.EnsureSuccessStatusCode();
    existingAssistant = await response?.Content.ReadFromJsonAsync<Assistant>()!;  
}
*/
Console.WriteLine($"Assistant ID: {existingAssistant!.Id}");
#endregion

#region Create a thread
Console.WriteLine("Creating thread...");
var newThreadResponse = await httpClient
    .PostAsync("v1/threads", new StringContent("", Encoding.UTF8, "application/json"));
newThreadResponse.EnsureSuccessStatusCode();
var newThread = await newThreadResponse.Content.ReadFromJsonAsync<CreateThreadResult>();
var threadId = newThread!.Id;

#endregion

#region Add message
Console.WriteLine("Adding message...");
var newMsgResponse = await httpClient.PostAsJsonAsync(
    $"v1/threads/{threadId}/messages", new CreateThreadMessage(
        """
        Hi! I will stay overnight in Vienna. What are the least known historical spots I can check out?
        Let your answer be based on the least number of mentions, or some other similar metrics.
        """));
newMsgResponse.EnsureSuccessStatusCode();
#endregion

#region Create run
Console.WriteLine("Creating run...");
var newRunResponse = await httpClient.PostAsJsonAsync(
    $"v1/threads/{threadId}/runs", new CreateRun(existingAssistant.Id!));
newRunResponse.EnsureSuccessStatusCode();
var newRun = await newRunResponse.Content.ReadFromJsonAsync<Run>();
var runId = newRun!.Id;
Console.WriteLine($"\tRun ID: {runId}");  
#endregion

#region Delete the thread
Console.WriteLine("Deleting the thread...");
await httpClient.DeleteAsync($"v1/threads/{threadId}");
#endregion

#region DTOs for OpenAI
// Note that normally, we would use Microsoft's Nuget package for OpenAI access (https://www.nuget.org/packages/Azure.AI.OpenAI). 
// However, the current version does not support the Beta APIs from OpenAI. Thererfore, we have to implement
// the DTOs ourselves. You can track the progress of the new OpenAI features in the Azure.AI.OpenAI package here:
// https://github.com/Azure/azure-sdk-for-net/issues/40347

record OaiResult<T>(
    T[] Data
);

record Assistant(
    string Name,
    string Description,
    string Model,
    string Instructions,
    FunctionToolEnvelope[] Tools)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }
}

record FunctionToolEnvelope(
    FunctionTool Function)
{
    public string Type => "function";
}

record FunctionTool(
    string Name,
    string Description,
    FunctionParameters Parameters
);

record FunctionParameters(
    Dictionary<string, FunctionParameter> Properties,
    string[] Required
)
{
    public string Type => "object";
}

record FunctionParameter(
    string Type,
    string Description
);

record CreateThread();

record CreateThreadResult(
    string Id
);

record CreateThreadMessage(
    string Content
)
{
    public string Role => "user";
}

record Message(
    string Id,
    MessageContent[] Content,
    [property: JsonPropertyName("thread_id")] string ThreadId,
    string Role,
    [property: JsonPropertyName("assistant_id")] string AssistantId,
    [property: JsonPropertyName("run_id")] string RunId
);

record MessageContent(
    MessageContentText Text
)
{
    public string Type => "text";
}

record MessageContentText(
    string Value
);

record CreateRun(
    [property: JsonPropertyName("assistant_id")] string AssistantId
);

record Run(
    string Id,
    [property: JsonPropertyName("thread_id")] string ThreadId,
    [property: JsonPropertyName("assistant_id")] string AssistantId,
    string Status,
    [property: JsonPropertyName("required_action")] RequiredAction RequiredAction,
    [property: JsonPropertyName("last_error")] string LastError
);

record RequiredAction(
    string Type,
    [property: JsonPropertyName("submit_tool_outputs")] SubmitToolOutputs? SubmitToolOutputs
);

record SubmitToolOutputs(
    [property: JsonPropertyName("tool_calls")] ToolCall[] ToolCalls
);

record ToolCall(
    string Id,
    FunctionToolCall Function
);

record FunctionToolCall(
    string Name,
    string Arguments
);

record VisitArguments(
    string TownName,
    string StreetName,
    string HouseNumber,
    string FamilyName,
    bool SuccessfullyVisited
);

record ToolsOutput(
    [property: JsonPropertyName("tool_call_id")] string ToolCallId,
    string Output
);

#endregion