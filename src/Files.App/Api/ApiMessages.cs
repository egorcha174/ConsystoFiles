// Consysto fork: the shape of what goes in and out of the control channel.

using System.Text.Json.Serialization;

namespace Files.App.Api
{
	/// <summary>One request: the name of a command and whatever that command needs.</summary>
	public sealed record ApiRequest(
		string? command,
		string? path = null,
		string? where = null,
		int? index = null,
		string? name = null,
		string[]? folders = null);

	/// <summary>One answer. <see cref="ok"/> says whether the command ran; the rest is filled in by those that report something.</summary>
	public sealed record ApiResponse(
		bool ok,
		string? error = null,
		ApiWindow? window = null);

	/// <summary>What the window looks like right now: its tabs and the panes of the current one.</summary>
	public sealed record ApiWindow(
		int selectedTab,
		ApiTab[] tabs,
		string[] panes,
		int activePane);

	public sealed record ApiTab(int index, string? title, string? path);

	[JsonSerializable(typeof(ApiRequest))]
	[JsonSerializable(typeof(ApiResponse))]
	[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
	internal sealed partial class ApiJsonContext : JsonSerializerContext
	{
	}
}
