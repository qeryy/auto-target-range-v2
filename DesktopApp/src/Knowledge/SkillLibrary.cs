// 灵枢桌面版 —— 技能库（内置 + 用户自定义）
//
// 技能分两层：
//   · **内置**：288 条，编进 exe 的 skills.json，只读。升级 exe 时整体替换。
//   · **用户**：%APPDATA%\SpiritScout\my-skills.json，可增删改。升级不动它。
//
// 分开的理由：内置库跟着版本走（作者维护），用户库跟着人走（自己积累）。
// 混在一个文件里的话，升级 exe 要么冲掉用户的东西，要么永远带不上新技能。
//
// 同名技能按 id 去重，**用户库优先**——用户可以覆盖内置的某条。
//
// 技能页和 kb_search 工具都走这里，保证「页面上看得到的，agent 就能查到」。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SpiritScout.Core;

namespace SpiritScout.Knowledge
{
    /// <summary>一条技能。</summary>
    public sealed class SkillEntry
    {
        public string Id { get; set; }
        public string Domain { get; set; }
        public string Description { get; set; }
        public string Body { get; set; }
        public string Tags { get; set; }

        /// <summary>来源：builtin / user。</summary>
        public string Source { get; set; }

        public SkillEntry()
        {
            Id = "";
            Domain = "";
            Description = "";
            Body = "";
            Tags = "";
            Source = "user";
        }
    }

    public sealed class SkillFile
    {
        public List<SkillEntry> Skills { get; set; }
        public SkillFile() { Skills = new List<SkillEntry>(); }
    }

    public static class SkillLibrary
    {
        private static List<SkillEntry> _merged;

        /// <summary>用户技能文件。</summary>
        public static string UserFile
        {
            get { return Path.Combine(AppPaths.DataRoot, "my-skills.json"); }
        }

        /// <summary>清缓存（用户增删后要调，否则页面还是旧的）。</summary>
        public static void Invalidate() { _merged = null; }

        /// <summary>内置技能（从 exe 的嵌入资源读）。</summary>
        public static List<SkillEntry> LoadBuiltin()
        {
            var list = new List<SkillEntry>();
            try
            {
                var asm = typeof(SkillLibrary).Assembly;
                Stream stream = asm.GetManifestResourceStream("skills.json");
                if (stream == null)
                {
                    // 开发态可能从磁盘跑
                    string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skills.json");
                    if (File.Exists(local)) stream = File.OpenRead(local);
                }
                if (stream == null) return list;

                using (stream)
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var dict = Json.AsDict(Json.Parse(reader.ReadToEnd()));
                    var arr = Json.Array(dict, "skills");
                    if (arr != null)
                    {
                        foreach (var item in arr)
                        {
                            var d = Json.AsDict(item);
                            if (d == null) continue;
                            string id = Json.Str(d, "skill_id");
                            if (id.Length == 0) continue;
                            list.Add(new SkillEntry
                            {
                                Id = id,
                                Domain = Json.Str(d, "domain", "未分类"),
                                Description = Json.Str(d, "description"),
                                Body = Json.Str(d, "body"),
                                Tags = Json.Str(d, "tags"),
                                Source = "builtin",
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ConfigManager.Log("读取内置技能库失败: " + ex.Message);
            }
            return list;
        }

        /// <summary>用户技能。</summary>
        public static List<SkillEntry> LoadUser()
        {
            try
            {
                var f = JsonStore.Read<SkillFile>(UserFile, null);
                if (f == null || f.Skills == null) return new List<SkillEntry>();
                foreach (var s in f.Skills)
                {
                    if (s == null) continue;
                    s.Source = "user";
                }
                return f.Skills;
            }
            catch (Exception ex)
            {
                ConfigManager.Log("读取用户技能库失败: " + ex.Message);
                return new List<SkillEntry>();
            }
        }

        public static void SaveUser(List<SkillEntry> skills)
        {
            if (skills == null) skills = new List<SkillEntry>();
            JsonStore.Write(UserFile, new SkillFile { Skills = skills });
            Invalidate();
        }

        /// <summary>合并：内置 + 用户（用户优先）。</summary>
        public static List<SkillEntry> All()
        {
            if (_merged != null) return _merged;

            var map = new Dictionary<string, SkillEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in LoadBuiltin())
            {
                map[s.Id] = s;
            }
            foreach (var s in LoadUser())
            {
                if (string.IsNullOrEmpty(s.Id)) continue;
                map[s.Id] = s;     // 用户库覆盖同名内置
            }

            var list = new List<SkillEntry>(map.Values);
            list.Sort((a, b) =>
            {
                int c = string.Compare(a.Domain, b.Domain, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
            });
            _merged = list;
            return list;
        }

        /// <summary>统计。</summary>
        public static void Stats(out int builtin, out int user)
        {
            builtin = LoadBuiltin().Count;
            user = LoadUser().Count;
        }
    }
}
