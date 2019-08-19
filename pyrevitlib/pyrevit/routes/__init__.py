"""Handles http api routing and serving with usage similar to flask."""
#pylint: disable=import-error,invalid-name,broad-except,dangerous-default-value,missing-docstring
from pyrevit.api import DB, UI
from pyrevit.coreutils import envvars
from pyrevit.userconfig import user_config

from pyrevit.routes import router
from pyrevit.routes import server


class API(object):
    """API root object

    Args:
        name (str): URL-safe unique root name of the API

    Example:
        >>> from pyrevit import routes
        >>> api = routes.API("pyrevit-core")
        >>> @api.route('/sessions/', methods=['POST'])
        >>> def reload_pyrevit(uiapp):
        ...     new_session_id = sessionmgr.reload_pyrevit()
        ...     return {"session_id": new_session_id}
    """
    def __init__(self, name):
        self.name = name

    def route(self, route_url, methods=['GET']):
        """Define a new route on this API."""
        def decorator(f):
            for method in methods:
                router.add_route(
                    api_name=self.name,
                    route=route_url,
                    method=method,
                    handler_func=f
                    )
            return f
        return decorator


def activate_routes():
    postern_server = envvars.get_pyrevit_env_var(envvars.POSTERN_SERVER)
    if not postern_server:
        port = user_config.core.get_option("routes_port", default_value=48884)
        postern_server = server.RoutesServer(ip='', port=port)
        envvars.set_pyrevit_env_var(envvars.POSTERN_SERVER, postern_server)
        postern_server.start()
