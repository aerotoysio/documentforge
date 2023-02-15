using DocumentForge.Document;

namespace DocumentForge.Index;

/// <summary>
/// Extracts values from a BsonDocument using dot-notation paths.
/// Examples: "name", "passenger.lastName", "flights[0].code", "flights[*].code"
/// </summary>
public static class JsonPathExtractor
{
    public static BsonValue Extract(BsonDocument doc, string path)
    {
        var segments = ParsePath(path);
        return ExtractValue(BsonValue.FromDocument(doc), segments, 0);
    }

    public static IEnumerable<BsonValue> ExtractAll(BsonDocument doc, string path)
    {
        var segments = ParsePath(path);
        return ExtractAllValues(BsonValue.FromDocument(doc), segments, 0);
    }

    private static BsonValue ExtractValue(BsonValue current, PathSegment[] segments, int index)
    {
        if (index >= segments.Length)
            return current;

        if (current.IsNull)
            return BsonValue.Null;

        var segment = segments[index];

        if (segment.IsArrayWildcard)
        {
            // [*] - return first match for single extract
            if (current.Type != BsonType.Array) return BsonValue.Null;
            var arr = current.AsArray;
            if (arr.Count == 0) return BsonValue.Null;
            return ExtractValue(arr[0], segments, index + 1);
        }

        if (segment.ArrayIndex >= 0)
        {
            if (current.Type != BsonType.Array) return BsonValue.Null;
            var arr = current.AsArray;
            if (segment.ArrayIndex >= arr.Count) return BsonValue.Null;
            return ExtractValue(arr[segment.ArrayIndex], segments, index + 1);
        }

        // Field access
        if (current.Type != BsonType.Document) return BsonValue.Null;
        var doc = current.AsDocument;
        var fieldValue = doc[segment.FieldName!];
        return ExtractValue(fieldValue, segments, index + 1);
    }

    private static IEnumerable<BsonValue> ExtractAllValues(BsonValue current, PathSegment[] segments, int index)
    {
        if (index >= segments.Length)
        {
            yield return current;
            yield break;
        }

        if (current.IsNull)
        {
            yield return BsonValue.Null;
            yield break;
        }

        var segment = segments[index];

        if (segment.IsArrayWildcard)
        {
            if (current.Type != BsonType.Array) yield break;
            var arr = current.AsArray;
            foreach (var item in arr.Items)
            {
                foreach (var val in ExtractAllValues(item, segments, index + 1))
                    yield return val;
            }
            yield break;
        }

        if (segment.ArrayIndex >= 0)
        {
            if (current.Type != BsonType.Array) yield break;
            var arr = current.AsArray;
            if (segment.ArrayIndex >= arr.Count) yield break;
            foreach (var val in ExtractAllValues(arr[segment.ArrayIndex], segments, index + 1))
                yield return val;
            yield break;
        }

        // Field access
        if (current.Type != BsonType.Document) yield break;
        var doc = current.AsDocument;
        var fieldValue = doc[segment.FieldName!];
        foreach (var val in ExtractAllValues(fieldValue, segments, index + 1))
            yield return val;
    }

    private static PathSegment[] ParsePath(string path)
    {
        var result = new List<PathSegment>();
        int i = 0;
        while (i < path.Length)
        {
            if (path[i] == '[')
            {
                int end = path.IndexOf(']', i);
                if (end < 0) break;
                var indexStr = path.Substring(i + 1, end - i - 1);
                if (indexStr == "*")
                    result.Add(new PathSegment { IsArrayWildcard = true });
                else if (int.TryParse(indexStr, out var idx))
                    result.Add(new PathSegment { ArrayIndex = idx });
                i = end + 1;
                if (i < path.Length && path[i] == '.') i++;
            }
            else
            {
                int dotPos = path.IndexOf('.', i);
                int bracketPos = path.IndexOf('[', i);
                int end;

                if (dotPos < 0 && bracketPos < 0) end = path.Length;
                else if (dotPos < 0) end = bracketPos;
                else if (bracketPos < 0) end = dotPos;
                else end = Math.Min(dotPos, bracketPos);

                var fieldName = path.Substring(i, end - i);
                if (fieldName.Length > 0)
                    result.Add(new PathSegment { FieldName = fieldName });

                i = end;
                if (i < path.Length && path[i] == '.') i++;
            }
        }
        return result.ToArray();
    }

    private struct PathSegment
    {
        public string? FieldName;
        public int ArrayIndex;
        public bool IsArrayWildcard;

        public PathSegment()
        {
            ArrayIndex = -1;
        }
    }
}
