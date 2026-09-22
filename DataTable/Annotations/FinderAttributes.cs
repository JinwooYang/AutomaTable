using System;

namespace DataTable.Annotations
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class FindByAttribute : Attribute
    {
        public FindByAttribute(params string[] keys)
        {
            if (keys == null || keys.Length == 0)
                throw new ArgumentException("Specify one or more member names.", nameof(keys));

            Keys = keys;
        }

        public string[] Keys { get; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class FindAllByAttribute : Attribute
    {
        public FindAllByAttribute(params string[] keys)
        {
            if (keys == null || keys.Length == 0)
                throw new ArgumentException("Specify one or more member names.", nameof(keys));

            Keys = keys;
        }

        public string[] Keys { get; }
    }
}
