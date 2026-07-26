namespace StellaCareApi.Models;

public record ActiveSearch(string Status, DateTimeOffset StartedAt, string? StartedBy, string? Note);
