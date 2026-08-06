using SancMeld.Api;

// The open-source host is intentionally this short. Everything of substance
// lives in SancMeld.Api / SancMeld.Core so the hosted edition can reuse it
// verbatim and only add authentication, tenancy and billing on top.
//
// Single-tenant, unauthenticated. Do not expose this directly to the internet.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSancMeld(builder.Configuration);

var app = builder.Build();

app.MapSancMeld();

app.Run();
