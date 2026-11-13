import React, { useCallback, useEffect, useState } from 'react';
import { useDispatch, useSelector } from 'react-redux';
import AppState from 'App/State/AppState';
import Alert from 'Components/Alert';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import { kinds } from 'Helpers/Props';
import { Statistics } from 'Movie/Movie';
import MovieEdition from 'Movie/MovieEdition';
import { fetchMovies } from 'Store/Actions/movieActions';
import { fetchMovieFiles } from 'Store/Actions/movieFileActions';
import translate from 'Utilities/String/translate';
import AddEditionModal from './AddEditionModal';
import EditEditionDefinitionModal, {
  DefinitionSelection,
} from './EditEditionDefinitionModal';
import EditionSubmissionsTable from './EditionSubmissionsTable';
import RemoveUnusedEditionModal, {
  RemovalSelection,
} from './RemoveUnusedEditionModal';
import SelectPrimaryEditionModal, {
  PrimarySelection,
} from './SelectPrimaryEditionModal';
import SetEditionMonitoringModal, {
  MonitoringSelection,
} from './SetEditionMonitoringModal';
import SetEditionQualityProfileModal, {
  ProfileSelection,
} from './SetEditionQualityProfileModal';

const columns = [
  { name: 'name', label: () => translate('Edition'), isVisible: true },
  { name: 'monitor', label: () => translate('Monitored'), isVisible: true },
  {
    name: 'profile',
    label: () => translate('QualityProfile'),
    isVisible: true,
  },
  {
    name: 'attributes',
    label: () => translate('EditionAttributes'),
    isVisible: true,
  },
  { name: 'file', label: () => translate('File'), isVisible: true },
  { name: 'actions', label: () => translate('Actions'), isVisible: true },
];

interface MovieEditionsTableProps {
  movieId: number;
  qualityProfileId: number;
  monitored: boolean;
  statistics?: Statistics;
}

function EditionMonitoringButton({
  edition,
  onSelect,
}: {
  edition: MovieEdition;
  onSelect(edition: MovieEdition): void;
}) {
  const onPress = useCallback(() => onSelect(edition), [edition, onSelect]);
  return (
    <Button
      isDisabled={edition.state === 'invalid' && !edition.monitored}
      onPress={onPress}
    >
      {translate(edition.monitored ? 'UnmonitorEdition' : 'MonitorEdition')}
    </Button>
  );
}

function EditionDefinitionButton({
  edition,
  isDisabled,
  onSelect,
}: {
  edition: MovieEdition;
  isDisabled: boolean;
  onSelect(edition: MovieEdition): void;
}) {
  const onPress = useCallback(() => onSelect(edition), [edition, onSelect]);
  return (
    <Button isDisabled={isDisabled} onPress={onPress}>
      {translate('EditEditionDefinition')}
    </Button>
  );
}

function EditionProfileButton({
  edition,
  isDisabled,
  onSelect,
}: {
  edition: MovieEdition;
  isDisabled: boolean;
  onSelect(edition: MovieEdition): void;
}) {
  const onPress = useCallback(() => onSelect(edition), [edition, onSelect]);
  return (
    <Button isDisabled={isDisabled} onPress={onPress}>
      {translate('ChangeEditionProfile')}
    </Button>
  );
}

function PrimaryEditionButton({
  edition,
  isDisabled,
  onSelect,
}: {
  edition: MovieEdition;
  isDisabled: boolean;
  onSelect(edition: MovieEdition): void;
}) {
  const onPress = useCallback(() => onSelect(edition), [edition, onSelect]);
  return (
    <Button isDisabled={isDisabled} onPress={onPress}>
      {translate('SelectPrimaryEdition')}
    </Button>
  );
}

function RemoveEditionButton({
  edition,
  isDisabled,
  onSelect,
}: {
  edition: MovieEdition;
  isDisabled: boolean;
  onSelect(edition: MovieEdition): void;
}) {
  const onPress = useCallback(() => onSelect(edition), [edition, onSelect]);
  return (
    <Button isDisabled={isDisabled} onPress={onPress}>
      {translate('RemoveUnusedEdition')}
    </Button>
  );
}

function MovieEditionsTable({
  movieId,
  qualityProfileId,
  monitored,
  statistics,
}: MovieEditionsTableProps) {
  const dispatch = useDispatch();
  const files = useSelector((state: AppState) => state.movieFiles.items);
  const [isAdding, setIsAdding] = useState(false);
  const [primarySelection, setPrimarySelection] = useState<PrimarySelection>();
  const [monitoringSelection, setMonitoringSelection] =
    useState<MonitoringSelection>();
  const [profileSelection, setProfileSelection] = useState<ProfileSelection>();
  const [removalSelection, setRemovalSelection] = useState<RemovalSelection>();
  const [definitionSelection, setDefinitionSelection] =
    useState<DefinitionSelection>();
  const [selectionError, setSelectionError] = useState<string>();
  const { data, error, isPending, isFetching, refetch } = useApiQuery<
    MovieEdition[]
  >({
    path: `/movieedition?movieId=${movieId}`,
    queryOptions: { staleTime: 0, refetchOnMount: 'always' },
  });
  useEffect(() => {
    refetch();
  }, [files, monitored, statistics, refetch]);
  const onAdd = useCallback(() => setIsAdding(true), []);
  const onClose = useCallback(() => setIsAdding(false), []);
  const onCreated = useCallback(() => {
    refetch();
    dispatch(fetchMovies({ id: movieId }));
    dispatch(fetchMovieFiles({ movieId }));
  }, [refetch, dispatch, movieId]);

  const onPrimaryClose = useCallback(() => setPrimarySelection(undefined), []);
  const onPrimarySelected = useCallback(() => {
    setPrimarySelection(undefined);
    setSelectionError(undefined);
    onCreated();
  }, [onCreated]);
  const onActionError = useCallback(
    (message: string) => {
      setPrimarySelection(undefined);
      setMonitoringSelection(undefined);
      setProfileSelection(undefined);
      setDefinitionSelection(undefined);
      setRemovalSelection(undefined);
      setSelectionError(message);
      onCreated();
    },
    [onCreated]
  );
  const onMonitoringClose = useCallback(
    () => setMonitoringSelection(undefined),
    []
  );
  const onMonitoringSelected = useCallback(() => {
    setMonitoringSelection(undefined);
    setSelectionError(undefined);
    onCreated();
  }, [onCreated]);
  const onMonitoringPress = useCallback(
    (target: MovieEdition) => {
      setSelectionError(undefined);
      setMonitoringSelection({ target, movieMonitored: monitored });
    },
    [monitored]
  );
  const onProfileClose = useCallback(() => setProfileSelection(undefined), []);
  const onDefinitionClose = useCallback(
    () => setDefinitionSelection(undefined),
    []
  );
  const onDefinitionSaved = useCallback(() => {
    setDefinitionSelection(undefined);
    setSelectionError(undefined);
    onCreated();
  }, [onCreated]);
  const onDefinitionPress = useCallback(
    (target: MovieEdition) => {
      setSelectionError(undefined);
      setDefinitionSelection({ target, movieMonitored: monitored });
    },
    [monitored]
  );
  const onProfileSelected = useCallback(() => {
    setProfileSelection(undefined);
    setSelectionError(undefined);
    onCreated();
  }, [onCreated]);
  const onProfilePress = useCallback(
    (target: MovieEdition) => {
      setSelectionError(undefined);
      setProfileSelection({ target, movieMonitored: monitored });
    },
    [monitored]
  );
  const onPrimaryPress = useCallback(
    (target: MovieEdition) => {
      const primary = data?.find((edition) => edition.isPrimary);
      if (primary) {
        setSelectionError(undefined);
        setPrimarySelection({ target, expectedPrimaryEditionId: primary.id });
      }
    },
    [data]
  );
  const onRemovalPress = useCallback(
    (target: MovieEdition) => {
      const primary = data?.find((edition) => edition.isPrimary);
      if (primary) {
        setSelectionError(undefined);
        setRemovalSelection({
          target,
          movieMonitored: monitored,
          expectedPrimaryEditionId: primary.id,
        });
      }
    },
    [data, monitored]
  );
  const onRemovalClose = useCallback(() => setRemovalSelection(undefined), []);
  const onRemoved = useCallback(() => {
    setRemovalSelection(undefined);
    setSelectionError(undefined);
    onCreated();
  }, [onCreated]);

  return (
    <>
      {isPending ? <LoadingIndicator /> : null}
      {selectionError ? (
        <Alert kind={kinds.DANGER}>{selectionError}</Alert>
      ) : null}
      {error ? (
        <Alert kind={kinds.DANGER}>{translate('UnableToLoadEditions')}</Alert>
      ) : null}
      {data?.length ? (
        <Table columns={columns}>
          <TableBody>
            {data.map((edition) => (
              <TableRow key={edition.id}>
                <TableRowCell>
                  {edition.name}{' '}
                  {edition.isPrimary ? (
                    <Label kind={kinds.INFO}>
                      {translate('PrimaryEdition')}
                    </Label>
                  ) : null}
                </TableRowCell>
                <TableRowCell>
                  {edition.effectiveMonitored
                    ? translate('Yes')
                    : translate('No')}
                  {edition.monitored && !monitored
                    ? ` (${translate('MovieMonitoringDisabled')})`
                    : ''}
                </TableRowCell>
                <TableRowCell>
                  {edition.qualityProfileName ?? edition.qualityProfileId}
                </TableRowCell>
                <TableRowCell>
                  {Object.entries(edition.attributes ?? {}).map(
                    ([key, value]) => (
                      <div key={key}>
                        {key}: {value}
                      </div>
                    )
                  )}
                </TableRowCell>
                <TableRowCell>
                  {edition.state === 'invalid' ? (
                    <Alert kind={kinds.DANGER}>
                      {edition.rejectionReason ?? translate('EditionInvalid')}
                    </Alert>
                  ) : (
                    edition.relativePath ?? translate('EditionMissing')
                  )}
                </TableRowCell>
                <TableRowCell>
                  <EditionProfileButton
                    edition={edition}
                    isDisabled={isFetching || !!error}
                    onSelect={onProfilePress}
                  />
                  <EditionDefinitionButton
                    edition={edition}
                    isDisabled={isFetching || !!error}
                    onSelect={onDefinitionPress}
                  />
                  <EditionMonitoringButton
                    edition={edition}
                    onSelect={onMonitoringPress}
                  />
                  {edition.isPrimary ? null : (
                    <PrimaryEditionButton
                      edition={edition}
                      isDisabled={
                        edition.state === 'invalid' ||
                        !data.some((target) => target.isPrimary)
                      }
                      onSelect={onPrimaryPress}
                    />
                  )}
                  {!edition.isPrimary &&
                  !edition.monitored &&
                  !edition.movieFileId &&
                  edition.state === 'missing' ? (
                    <RemoveEditionButton
                      edition={edition}
                      isDisabled={
                        isFetching ||
                        !!error ||
                        !data.some((target) => target.isPrimary)
                      }
                      onSelect={onRemovalPress}
                    />
                  ) : null}
                </TableRowCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : null}
      {data && !data.length ? (
        <Alert>{translate('NoEditionsConfigured')}</Alert>
      ) : null}
      <p>{translate('PrimaryEditionHelpText')}</p>
      <p>{translate('EditionMonitoringHelpText')}</p>
      <p>{translate('AddEditionHelpText')}</p>
      <p>{translate('RemoveUnusedEditionHelpText')}</p>
      <Button isDisabled={!data || !!error} onPress={onAdd}>
        {translate('AddEdition')}
      </Button>
      <SelectPrimaryEditionModal
        movieId={movieId}
        selection={primarySelection}
        onSelected={onPrimarySelected}
        onError={onActionError}
        onClose={onPrimaryClose}
      />
      <EditionSubmissionsTable movieId={movieId} />
      <RemoveUnusedEditionModal
        movieId={movieId}
        selection={removalSelection}
        onRemoved={onRemoved}
        onError={onActionError}
        onClose={onRemovalClose}
      />
      <SetEditionMonitoringModal
        movieId={movieId}
        selection={monitoringSelection}
        onSelected={onMonitoringSelected}
        onError={onActionError}
        onClose={onMonitoringClose}
      />
      {profileSelection ? (
        <SetEditionQualityProfileModal
          movieId={movieId}
          selection={profileSelection}
          onSelected={onProfileSelected}
          onError={onActionError}
          onClose={onProfileClose}
        />
      ) : null}
      {definitionSelection ? (
        <EditEditionDefinitionModal
          selection={definitionSelection}
          onSaved={onDefinitionSaved}
          onError={onActionError}
          onClose={onDefinitionClose}
        />
      ) : null}
      <AddEditionModal
        isOpen={isAdding}
        movieId={movieId}
        qualityProfileId={qualityProfileId}
        onCreated={onCreated}
        onModalClose={onClose}
      />
    </>
  );
}

export default MovieEditionsTable;
