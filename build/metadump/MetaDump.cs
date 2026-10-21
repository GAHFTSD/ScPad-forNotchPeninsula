// 元数据比对小工具：把某些 dll 里"指向 NotchPeninsula 程序集"的成员引用
// （名字 + 签名 blob）导出来，用来确认插件调用的宿主成员签名和真实插件一致。
// 只用 System.Reflection.Metadata，不加载任何程序集。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: MetaDump <out.txt> <dll> [dll...]");
            return 2;
        }

        var lines = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            try { Dump(args[i], lines); }
            catch (Exception ex) { lines.Add("!ERR\t" + args[i] + "\t" + ex.Message); }
        }

        File.WriteAllLines(args[0], lines.ToArray());
        Console.WriteLine("wrote " + args[0] + " (" + lines.Count.ToString() + " records)");
        return 0;
    }

    private static void Dump(string path, List<string> lines)
    {
        using (var stream = File.OpenRead(path))
        using (var pe = new PEReader(stream))
        {
            MetadataReader md = pe.GetMetadataReader();
            string file = Path.GetFileName(path);

            foreach (MemberReferenceHandle handle in md.MemberReferences)
            {
                MemberReference mr = md.GetMemberReference(handle);
                string parent = ParentName(md, mr.Parent);
                if (parent == null) continue;
                if (parent.IndexOf("NotchPeninsula", StringComparison.Ordinal) != 0) continue;

                byte[] blob = md.GetBlobBytes(mr.Signature);
                lines.Add(file + "\t" + parent + "::" + md.GetString(mr.Name) + "\t" + Hex(blob));
            }
        }
    }

    private static string ParentName(MetadataReader md, EntityHandle parent)
    {
        if (parent.Kind == HandleKind.TypeReference)
        {
            TypeReference tr = md.GetTypeReference((TypeReferenceHandle)parent);
            string name = md.GetString(tr.Name);
            string ns = md.GetString(tr.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        if (parent.Kind == HandleKind.TypeSpecification) return "<typespec>";
        return null;
    }

    private static string Hex(byte[] bytes)
    {
        var text = new System.Text.StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("X2"));
        return text.ToString();
    }
}
