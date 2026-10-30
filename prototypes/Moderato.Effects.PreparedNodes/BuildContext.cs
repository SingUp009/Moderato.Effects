sealed class BuildContext(params object[] pools)
{
    public T? Find<T>() where T : class
    {
        foreach (var pool in pools)
            if (pool is T typed) return typed;
        return null;
    }
}
