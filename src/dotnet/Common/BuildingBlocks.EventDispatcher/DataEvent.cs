namespace BuildingBlocks.EventDispatcher;

public class DataEvent
{
    public string Subject { get; set; }
    public string? CustomAction { get; set; }
    public DataEventAction Action { get; set; }
    public object[] Payload { get; set; }

    public Dictionary<string, object> MetaData { get; set; } = new();


    public DataEvent(DataEventAction action, string subject, IEnumerable<object>? payload = null)
    {
        Action = action;
        Subject = subject;
        Payload = payload?.ToArray() ?? [];
    }

    public DataEvent AddMetaData(string key, object value)
    {
        MetaData[key] = value;
        return this;
    }

    public DataEvent ModifyIfType<T>(Func<T, object> modify)
    {
        var newPayload = new List<object>();

        foreach (var p in Payload)
        {
            if (p is T t)
            {
                var modified = modify(t);
                newPayload.Add(modified);
            }
            else
            {
                newPayload.Add(p);
            }
        }
        Payload = newPayload.ToArray();
        return this;
    }

    public static DataEvent Created(string subject, object? payload = null)
    {
        return Created(subject, ArrayHelper.WrapInArray(payload));
    }
    public static DataEvent Created(string subject, IEnumerable<object>? payload)
    {
        return new DataEvent(DataEventAction.Created, subject, payload);
    }

    public static DataEvent Updated(string subject, object? payload = null)
    {
        return Updated(subject, ArrayHelper.WrapInArray(payload));
    }
    public static DataEvent Updated(string subject, IEnumerable<object>? payload)
    {
        return new DataEvent(DataEventAction.Updated, subject, payload);
    }

    public static DataEvent Deleted(string subject, object? payload = null)
    {
        return Deleted(subject, ArrayHelper.WrapInArray(payload));
    }
    public static DataEvent Deleted(string subject, IEnumerable<object>? payload)
    {
        return new DataEvent(DataEventAction.Deleted, subject, payload);
    }

    public static DataEvent Custom(string subject, string customAction, object? payload = null)
    {
        return Custom(subject, customAction, ArrayHelper.WrapInArray(payload));
    }
    public static DataEvent Custom(string subject, string customAction, IEnumerable<object> payload)
    {
        var de = new DataEvent(DataEventAction.Custom, subject, payload);
        de.CustomAction = customAction;
        return de;
    }

    public static DataEvent FullSync(string subject, object? payload = null)
    {
        return FullSync(subject, ArrayHelper.WrapInArray(payload));
    }
    public static DataEvent FullSync(string subject, IEnumerable<object> payload)
    {
        return new DataEvent(DataEventAction.FullSync, subject, payload);
    }
}
