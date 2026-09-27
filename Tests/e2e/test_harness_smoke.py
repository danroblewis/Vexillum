"""Self-test of the e2e harness: server + one real client, state visible on both sides."""
import pytest

from conftest import local_player_state, server_player_state, wait_until, press


@pytest.mark.e2e
def test_server_and_client_agree_on_the_local_player(server, client):
    assert server.alive and client.alive
    me = wait_until(lambda: local_player_state(client), timeout=20, message="no local player on the client")
    assert me["name"], me
    assert me["class"] in ("Green", "Blue"), me
    assert me["health"] == 100.0

    them = wait_until(lambda: server_player_state(server, me["name"]), timeout=10,
                      message="server does not list the client's player")
    assert them["entity_id"] == me["entity_id"]
    assert them["class"] == me["class"]
    assert not them["bot"]

    # the server keeps stepping at 60 Hz and the client received the terrain
    f = server.frame()
    wait_until(lambda: server.frame() > f, timeout=5, message="server frame does not advance")
    assert "Set terrain state" in client.logs(grep="Set terrain state")

    # input reaches the game: a key press on the update thread does not throw
    press(client, "D", hold_ms=100)
