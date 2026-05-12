// Removed in the Phase-3 implementation round. The Anthropic-specific wire DTOs and the
// HTTP call live inside MacMonitor.Agent now (`AnthropicClient`); the Core abstraction
// at this level was over-engineered. `ITriageService` remains the agent-shaped boundary
// the orchestrator depends on.
