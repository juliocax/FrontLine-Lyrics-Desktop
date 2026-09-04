import asyncio

import pytest

from engine.task_utils import spawn_task, _background_tasks


@pytest.mark.asyncio
async def test_spawn_task_runs_the_coroutine():
    result = {}

    async def work():
        result["ran"] = True

    task = spawn_task(work())
    await task
    assert result["ran"] is True


@pytest.mark.asyncio
async def test_spawn_task_holds_a_strong_reference_until_done():
    async def work():
        await asyncio.sleep(0.01)

    task = spawn_task(work())
    assert task in _background_tasks
    await task
    # done_callback should have removed it from the tracking set.
    assert task not in _background_tasks


@pytest.mark.asyncio
async def test_spawn_task_propagates_exceptions_to_the_task():
    async def failing():
        raise ValueError("boom")

    task = spawn_task(failing())
    with pytest.raises(ValueError):
        await task
