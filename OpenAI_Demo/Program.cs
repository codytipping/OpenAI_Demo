using Microsoft.Extensions.FileProviders;
using System.Net.Http.Headers;
using System.Reflection;
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
    "Cooper, an AI Assistant",
    "Assistant used in a fundraising scenario helping fundraisers to store visits",
    "gpt-3.5-turbo-1106",
    """
    You are a helpful assistant supporting people doing fundraising by visiting 
    people in their community. Fundraisers will tell you about which households
    they visited (town name, street name, house number, family name). Additionally,
    they will tell you whether they met someone or not. Fundraising happens in Orlando, Florida,
    so town and street names are in English.

    Try to identify the necessary data about the household and the flag whether someone
    was met or not. Ask the fundraiser questions until you have all the necessary data.
    Once you have the data, call the function 'store_visit' with the data as parameters.
    """,
    [
        new(
            new(
                "store_visit",
                "Stores a visit in the database",
                new(
                    Properties: new()
                    {
                        ["townName"] = new("string", "Name of the town of the visited household"),
                        ["streetName"] = new("string", "Name of the street of the visited household"),
                        ["houseNumber"] = new("string", "House number of the visited household"),
                        ["familyName"] = new("string", "Family name of the visited household"),
                        ["successfullyVisited"] = new("boolean", "Value indicating whether someone was met or not"),
                    },
                    Required: ["townName", "streetName", "houseNumber", "familyName", "successfullyVisited"])
            )
        )
    ]
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
        Hi! I just visited the family Tipping in Orlando at 3246 Touraine Avenue, 32812. 
        They were at home. 
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

#region Wait for status completed
Console.WriteLine("Waiting for status completed...");
while (newRun.Status is not "completed" and not "requires_action")
{
    await Task.Delay(1000);
    Console.WriteLine("\tChecking status...");
    newRun = await httpClient.GetFromJsonAsync<Run>($"v1/threads/{threadId}/runs/{runId}");
    Console.WriteLine($"\tStatus: {newRun!.Status}");
}
#endregion

#region Print the message result
switch (newRun.Status)
{
    case "completed":
        {
            Console.WriteLine("Listing messages of the thread...");
            var messages = await httpClient
                .GetFromJsonAsync<OaiResult<Message>>($"v1/threads/{threadId}/messages");
            foreach (var message in messages!.Data)
            {
                foreach (var content in message.Content)
                {
                    Console.WriteLine($"\t\t{message.Role}: {content.Text.Value}");
                }
            }
            break;
        }
    case "requires_action":
        {
            break;
        }
}
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