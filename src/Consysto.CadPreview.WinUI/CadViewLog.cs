namespace Consysto.CadPreview.WinUI;

/// <summary>Where the views report work that failed in the background, for the host's log.</summary>
public static class CadViewLog
{
	public static Action<string, Exception?>? Write { get; set; }
}
