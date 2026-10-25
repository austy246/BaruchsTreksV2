namespace BaruchsTreks.Strava;

// Zprávy jsou určené uživateli (zobrazí se v hlášce), proto neobsahují tělo odpovědi Stravy ani tokeny.

/// <summary>Chybí <c>Strava:ClientId</c> nebo <c>Strava:ClientSecret</c> (<c>StravaNotConfigured</c>).</summary>
public sealed class StravaNotConfigured(string message) : Exception(message);

/// <summary>Uživatel nemá uložené tokeny, nebo je Strava odmítla (<c>StravaNotConnected</c>).</summary>
public sealed class StravaNotConnected(string message) : Exception(message);

/// <summary>Strava vrátila chybu (<c>StravaError</c>).</summary>
public sealed class StravaError(string message) : Exception(message);
