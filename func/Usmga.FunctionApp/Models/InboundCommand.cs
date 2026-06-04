namespace Usmga.FunctionApp.Models;

public enum InboundCommandKind { NewRequest, Approve, Changes, Invalid }

public sealed record InboundCommand(InboundCommandKind Kind, string? Code, string? ApprovalNonce, string Text);
