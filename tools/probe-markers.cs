// Deep-profile markers exist for every AiActorController method, including the eight coroutines.
// Enumerate them so the measurement can sum recorders over the real names rather than guessing at
// "AiActorController.Update()", which Recorder.Get happily invents and never fills in.
var names = new List<string>();
UnityEngine.Profiling.Sampler.GetNames(names);

var ai = names.Where(n => n != null
        && n.IndexOf("AiActorController", StringComparison.Ordinal) >= 0)
    .OrderBy(n => n).ToList();

Out("AiActorController samplers: " + ai.Count);
foreach (string n in ai) Out("  " + n);

// get_Current is the enumerator property, not the body; counting it double-counts each MoveNext.
var bodies = ai.Where(n => n.IndexOf("get_Current", StringComparison.Ordinal) < 0).ToList();
Out("bodies only (excluding get_Current): " + bodies.Count);

Out("deep profiling appears " + (ai.Count > 0 ? "ON" : "OFF"));
