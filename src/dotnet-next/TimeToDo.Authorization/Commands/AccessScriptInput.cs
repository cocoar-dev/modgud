namespace TimeToDo.Authorization.Commands;

/// <summary>
/// Create/update-side access-script payload. Only the source <see cref="Script"/>
/// is supplied — the handler transpiles to JavaScript and stores both sides on
/// the persisted <see cref="Access.ResourceAccessScript"/>.
/// </summary>
public record AccessScriptInput(string ResourceType, string? Script);
