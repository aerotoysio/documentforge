using Xunit;
using DocumentForge.Cli.Commands;

namespace DocumentForge.Tests;

/// <summary>
/// Covers the collection-name whitelist used by the HTTP routes in
/// ServeCommand (issue #102). Collection names arrive as URL path segments
/// and are interpolated into SQL, so anything outside the engine lexer's
/// identifier rule must be rejected before it reaches the query engine.
/// </summary>
public class CollectionNameValidationTests
{
    [Theory]
    [InlineData("orders")]
    [InlineData("Orders")]
    [InlineData("_system")]
    [InlineData("orders_2026")]
    [InlineData("a")]
    [InlineData("_")]
    [InlineData("flight_legs_v2")]
    // Namespaced names are first-class: clients group collections by module
    // (aerobus uses catalogue.flights, orders.orders, policystudio.spaces, …).
    // The query lexer resolves dotted identifiers, so these stay queryable.
    [InlineData("catalogue.flights")]
    [InlineData("orders.orders")]
    [InlineData("policystudio.spaces")]
    [InlineData("a.b.c")]
    public void ValidNames_AreAccepted(string name)
    {
        Assert.True(ServeCommand.IsValidCollectionName(name));
    }

    [Fact]
    public void MaxLengthName_IsAccepted()
    {
        Assert.True(ServeCommand.IsValidCollectionName("a" + new string('b', 127)));
    }

    [Fact]
    public void OverlongName_IsRejected()
    {
        Assert.False(ServeCommand.IsValidCollectionName("a" + new string('b', 128)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1orders")]                                  // must not start with a digit
    [InlineData("orders-archive")]                           // hyphen is not a SQL identifier char
    [InlineData(".orders")]                                  // no leading dot
    [InlineData("orders.")]                                  // no trailing dot
    [InlineData("orders..archive")]                          // no empty segment
    [InlineData("orders.1archive")]                          // segments must not start with a digit
    [InlineData("orders name")]
    [InlineData("orders'")]
    [InlineData("orders\"")]
    [InlineData("orders;")]
    [InlineData("orders--")]
    [InlineData("naïve")]                                    // non-ASCII letters
    public void InvalidNames_AreRejected(string name)
    {
        Assert.False(ServeCommand.IsValidCollectionName(name));
    }

    [Theory]
    // The audit's attack shape: GET /collections/{name} builds "SELECT * FROM {name}".
    [InlineData("orders WHERE 1=1")]
    [InlineData("orders; DROP TABLE orders")]
    [InlineData("orders UNION SELECT * FROM _system")]
    [InlineData("orders LIMIT 1 --")]
    [InlineData("orders/*comment*/")]
    [InlineData("orders%20WHERE%201=1")]                     // pre-decoded percent form
    public void InjectionPayloads_AreRejected(string name)
    {
        Assert.False(ServeCommand.IsValidCollectionName(name));
    }
}
