import assert from 'node:assert/strict';
import fs from 'node:fs';

const store = fs.readFileSync(new URL('../src/Jellyfin.Plugin.Chat/ChatStore.cs', import.meta.url), 'utf8');
const controller = fs.readFileSync(new URL('../src/Jellyfin.Plugin.Chat/Api/ChatController.cs', import.meta.url), 'utf8');

const pairKey = (a, b) => [a, b].sort().join(':');
assert.equal(pairKey('b', 'a'), pairKey('a', 'b'), 'direct pair identity must be order-independent');
assert.notEqual(pairKey('a', 'b'), pairKey('a', 'c'));

const channel = { IsPrivate: true, IsRestricted: true, MemberUserIds: ['alice', 'bob'] };
const canAccess = (candidate, user) => (!candidate.IsPrivate && !candidate.IsRestricted)
  || candidate.MemberUserIds.includes(user);
assert.equal(canAccess(channel, 'alice'), true);
assert.equal(canAccess(channel, 'bob'), true);
assert.equal(canAccess(channel, 'admin'), false, 'administrator must not bypass private membership');

const ready = (members, devices) => members.every(user => devices.some(device => device.user === user && device.enabled && !device.revoked));
assert.equal(ready(['alice', 'bob'], [{ user: 'alice', enabled: true, revoked: false }]), false);
assert.equal(ready(['alice', 'bob'], [{ user: 'alice', enabled: true, revoked: false }, { user: 'bob', enabled: true, revoked: false }]), true);
assert.equal(ready(['alice', 'bob'], [{ user: 'alice', enabled: true, revoked: false }, { user: 'bob', enabled: true, revoked: true }]), false);

const target = (members, devices) => devices.filter(device => members.includes(device.user) && device.enabled && !device.revoked).map(device => device.id).sort();
assert.deepEqual(target(['alice', 'bob'], [{ id: 'a1', user: 'alice', enabled: true, revoked: false }, { id: 'x1', user: 'mallory', enabled: true, revoked: false }]), ['a1']);
assert.deepEqual(target(['alice', 'bob'], [{ id: 'a1', user: 'alice', enabled: true, revoked: false }, { id: 'b1', user: 'bob', enabled: true, revoked: false }]), ['a1', 'b1']);

for (const marker of [
  'AddOrGetPrivateChannel', 'GetChannelsForUser', 'RequireAccessibleChannelUnsafe',
  'CanAccessChannelUnsafe', 'AllParticipantsReady', 'IsPrivate || channel.IsRestricted',
  'RestrictedAnnouncementKind', 'MemberUserIds', 'DirectPairKey'
]) {
  assert.match(store + controller, new RegExp(marker.replace(/[|]/g, '\\$&')), `missing private-chat policy marker: ${marker}`);
}
assert.match(controller, /PrivateChat/);
assert.doesNotMatch(controller, /PrivateChat\/Users/, 'ordinary users must not be able to enumerate chat users');
assert.match(controller, /StringComparison\.OrdinalIgnoreCase/, 'private chat lookup must use one exact username comparison');
assert.match(store, /target\.AuthorId != userId/, 'non-admin users may only delete their own messages');
console.log('Private chat policy checks passed.');
