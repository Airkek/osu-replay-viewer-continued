using AutoMapper.Internal;
using osu.Framework.Extensions.IEnumerableExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace osu_replay_renderer_netcore
{
    static class DrawablesUtils
    {
        public static void RemoveRecursive(this Container<Drawable> container, Predicate<Drawable> predicate)
        {
            container.RemoveAll(predicate, true);
            container.ForEach(drawable =>
            {
                if (drawable is Container<Drawable> container2) RemoveRecursive(container2, predicate);
                else if (drawable is FillFlowContainer fillFlow) RemoveRecursive(fillFlow, predicate);
            });
        }

        public static Drawable GetInternalChild(CompositeDrawable drawable)
        {
            PropertyInfo internalChildProperty = typeof(CompositeDrawable).GetProperty("InternalChild", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo getter = internalChildProperty.GetGetMethod(nonPublic: true);
            return getter.Invoke(drawable, null) as Drawable;
        }

        public static IReadOnlyList<Drawable> GetInternalChildren(CompositeDrawable drawable)
        {
            PropertyInfo internalChildrenProperty = typeof(CompositeDrawable).GetProperty("InternalChildren", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo getter = internalChildrenProperty.GetGetMethod(nonPublic: true);
            return getter.Invoke(drawable, null) as IReadOnlyList<Drawable>;
        }
    }
}
