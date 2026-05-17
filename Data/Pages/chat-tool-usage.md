# Chat tool usage

It looks like all of the wiki pages are getting sent to the AI every time (which seems like an easy way to bloat the context window). Please review and fix
```
**Considering tool calls**

It seems like the app-intercepted tool isn't compatible with the functions tool. So, maybe we should include both: I can call the functions.report_intent and invoke the memorysmith tool through special JSON. However, given the complexity, it might be simpler to ask a clarifying question first. I’ll avoid tool calls for now and propose a default query for “semantic search” to get the top results. I’ll keep my response concise and ask for confirmation.
```