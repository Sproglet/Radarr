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
import translate from 'Utilities/String/translate';
import AddEditionModal from './AddEditionModal';

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
];

interface MovieEditionsTableProps {
  movieId: number;
  qualityProfileId: number;
  monitored: boolean;
  statistics?: Statistics;
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
  const { data, error, isPending, refetch } = useApiQuery<MovieEdition[]>({
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
  }, [refetch, dispatch, movieId]);

  return (
    <>
      {isPending ? <LoadingIndicator /> : null}
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
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : null}
      {data && !data.length ? (
        <Alert>{translate('NoEditionsConfigured')}</Alert>
      ) : null}
      <p>{translate('AddEditionHelpText')}</p>
      <Button isDisabled={!data || !!error} onPress={onAdd}>
        {translate('AddEdition')}
      </Button>
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
