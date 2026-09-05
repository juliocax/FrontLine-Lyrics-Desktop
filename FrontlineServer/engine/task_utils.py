"""
Small asyncio helper shared by music_manager.py and ws_server.py.

Kept in its own module (instead of e.g. ws_server.py) so that neither
music_manager.py nor ws_server.py has to import the other just to reuse
spawn_task.
"""

import asyncio

# asyncio only keeps a weak reference to a task's coroutine; if nothing else
# holds a strong reference, the task can be garbage-collected before it
# finishes. This set is that strong reference.
_background_tasks: set = set()


def spawn_task(coro):
    """Create a background task without risking it being GC'd before it finishes."""
    task = asyncio.create_task(coro)
    _background_tasks.add(task)
    task.add_done_callback(_background_tasks.discard)
    return task
