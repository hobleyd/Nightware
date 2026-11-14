namespace Nightware.Application.Tasks;

/// <summary>An HTTP <c>Content-Range</c> (<c>bytes first-last/total</c>).</summary>
public sealed record ContentRange(long First, long Last, long Total);
