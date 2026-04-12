var __extends = (this && this.__extends) || (function () {
    var extendStatics = function (d, b) {
        extendStatics = Object.setPrototypeOf ||
            ({ __proto__: [] } instanceof Array && function (d, b) { d.__proto__ = b; }) ||
            function (d, b) { for (var p in b) if (Object.prototype.hasOwnProperty.call(b, p)) d[p] = b[p]; };
        return extendStatics(d, b);
    };
    return function (d, b) {
        if (typeof b !== "function" && b !== null)
            throw new TypeError("Class extends value " + String(b) + " is not a constructor or null");
        extendStatics(d, b);
        function __() { this.constructor = d; }
        d.prototype = b === null ? Object.create(b) : (__.prototype = b.prototype, new __());
    };
})();
var __rest = (this && this.__rest) || function (s, e) {
    var t = {};
    for (var p in s) if (Object.prototype.hasOwnProperty.call(s, p) && e.indexOf(p) < 0)
        t[p] = s[p];
    if (s != null && typeof Object.getOwnPropertySymbols === "function")
        for (var i = 0, p = Object.getOwnPropertySymbols(s); i < p.length; i++) {
            if (e.indexOf(p[i]) < 0 && Object.prototype.propertyIsEnumerable.call(s, p[i]))
                t[p[i]] = s[p[i]];
        }
    return t;
};
import "@testing-library/jest-dom/vitest";
// React Router's data routers (including createMemoryRouter) construct a
// `new Request(url, { signal })` on every navigation. Under vitest's jsdom
// environment the `AbortSignal` Node hands to the router is from jsdom's
// realm, while the `Request` constructor we get is undici's, and undici
// strictly refuses signals from other realms. We do not care about abort
// propagation in tests, so wrap the global Request to silently strip the
// signal before delegating to the real constructor.
{
    var OriginalRequest = globalThis.Request;
    if (OriginalRequest) {
        var PatchedRequest = /** @class */ (function (_super) {
            __extends(PatchedRequest, _super);
            function PatchedRequest(input, init) {
                var _this = this;
                if (init && "signal" in init) {
                    var _ignored = init.signal, rest = __rest(init, ["signal"]);
                    _this = _super.call(this, input, rest) || this;
                }
                else {
                    _this = _super.call(this, input, init) || this;
                }
                return _this;
            }
            return PatchedRequest;
        }(OriginalRequest));
        globalThis.Request = PatchedRequest;
    }
}
